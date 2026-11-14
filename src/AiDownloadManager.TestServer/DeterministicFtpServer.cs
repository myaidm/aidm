using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AiDownloadManager.TestServer;

/// <summary>Fault-injection modes for the deterministic FTP test server.</summary>
public enum FtpServerBehavior
{
    /// <summary>Correct FTP behaviour: SIZE, REST, RETR, PASV/EPSV.</summary>
    Normal,
    /// <summary>FEAT omits REST and the REST command is rejected (502).</summary>
    NoRest,
    /// <summary>Closes the data connection after N bytes (first K transfers only).</summary>
    DropData,
    /// <summary>Accepts REST (350) but RETR ignores the restart offset and sends the whole file.</summary>
    IgnoreRest,
    /// <summary>Sends a garbage greeting and closes the connection.</summary>
    BadGreeting
}

public sealed record FtpServerOptions(
    long FileSizeBytes,
    FtpServerBehavior Behavior = FtpServerBehavior.Normal,
    long DropAfterBytes = 0,
    int DropFirstNTransfers = 1,
    int Seed = 42,
    string UserName = "test",
    string Password = "test",
    bool EnableTls = false,
    /// <summary>Listen on IPv6 loopback (::1). Default true: this is the
    /// only loopback family .NET TCP can use in the Muse sandbox
    /// (IPv4 TCP is redirected to the egress proxy).</summary>
    bool ListenIPv6 = true,
    /// <summary>Listen on IPv4 loopback (127.0.0.1) as well.</summary>
    bool ListenIPv4 = false,
    /// <summary>
    /// When set, the server also accepts FTP control connections on a
    /// Unix domain socket at this path (same command handling; data
    /// connections still use TCP). Mirrors
    /// <see cref="TestServerOptions.UnixSocketPath"/>.
    /// </summary>
    string? UnixSocketPath = null);

/// <summary>
/// Deterministic local FTP test server on raw <see cref="Socket"/>s.
/// Listens on TCP loopback (IPv6 ::1 by default, optionally IPv4
/// 127.0.0.1) and, when configured, on a Unix domain socket. Serves
/// one generated file and implements USER/PASS, TYPE I, SYST, FEAT,
/// PWD/CWD, SIZE, REST, RETR, PASV, EPSV, PBSZ/PROT and AUTH TLS
/// (explicit FTPS with a generated self-signed certificate), plus
/// fault injection. Hand-rolled on purpose: a minimal server gives
/// exact control over malformed responses, which a framework server
/// would refuse to produce. Never used for anything except tests.
/// </summary>
public sealed class DeterministicFtpServer : IAsyncDisposable
{
    private readonly string _filePath;
    private readonly FtpServerOptions _options;
    private readonly List<Socket> _listeners = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _acceptLoops = new();
    private readonly X509Certificate2? _certificate;
    private int _dropCount;

    public DeterministicFtpServer(string filePath, FtpServerOptions options)
    {
        _filePath = filePath;
        _options = options;

        if (options.EnableTls)
        {
            _certificate = CreateTestCertificate();
            CertificateThumbprint = _certificate.Thumbprint;
        }

        if (options.ListenIPv6)
        {
            var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            _listeners.Add(listener);
        }
        if (options.ListenIPv4)
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listeners.Add(listener);
        }
        if (options.UnixSocketPath is not null)
        {
            try { File.Delete(options.UnixSocketPath); } catch { }
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(options.UnixSocketPath));
            _listeners.Add(listener);
        }
        if (_listeners.Count == 0)
            throw new ArgumentException("At least one listener is required.", nameof(options));
    }

    /// <summary>SHA-1 thumbprint of the test TLS certificate (null when TLS is off).</summary>
    public string? CertificateThumbprint { get; }

    /// <summary>TCP port of the first TCP listener (IPv6 preferred).</summary>
    public int Port { get; private set; }

    /// <summary>Base URL without credentials, e.g. <c>ftp://[::1]:12345/</c>.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Full URL of the served file, with test credentials.</summary>
    public string FileUrl =>
        $"ftp://{_options.UserName}:{_options.Password}@{HostForUrl()}:{Port}/file.bin";

    /// <summary>Full FTPS URL of the served file, with test credentials.</summary>
    public string FtpsFileUrl =>
        $"ftps://{_options.UserName}:{_options.Password}@{HostForUrl()}:{Port}/file.bin";

    private string HostForUrl()
    {
        var first = _listeners[0];
        return first.AddressFamily switch
        {
            AddressFamily.InterNetworkV6 => "[::1]",
            AddressFamily.InterNetwork => "127.0.0.1",
            _ => "[::1]"
        };
    }

    public async Task StartAsync()
    {
        foreach (var listener in _listeners)
        {
            listener.Listen(backlog: 128);
            if (listener.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && Port == 0)
            {
                Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
                BaseUrl = listener.AddressFamily == AddressFamily.InterNetworkV6
                    ? $"ftp://[::1]:{Port}/"
                    : $"ftp://127.0.0.1:{Port}/";
            }
            _acceptLoops.Add(AcceptLoopAsync(listener, _cts.Token));
        }
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(ct);
                _ = HandleControlConnectionAsync(socket, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task HandleControlConnectionAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        {
            Stream stream = new NetworkStream(socket, ownsSocket: true);
            try
            {
                if (_options.Behavior == FtpServerBehavior.BadGreeting)
                {
                    await WriteLineAsync(stream, "999 This is not an FTP server", ct);
                    return;
                }

                await WriteLineAsync(stream, "220 AiDownloadManager test FTP server ready", ct);

                var session = new SessionState();
                var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                        return;
                    var (verb, argument) = SplitCommand(line);

                    switch (verb)
                    {
                        case "USER":
                            session.UserName = argument;
                            await ReplyAsync(writer, "331 User name okay, need password.", ct);
                            break;
                        case "PASS":
                            session.Authenticated =
                                session.UserName == _options.UserName && argument == _options.Password;
                            await ReplyAsync(writer,
                                session.Authenticated ? "230 Login successful." : "530 Login incorrect.", ct);
                            break;
                        case "SYST":
                            await ReplyAsync(writer, "215 UNIX Type: L8", ct);
                            break;
                        case "FEAT":
                            await ReplyAsync(writer, FeatReply(), ct);
                            break;
                        case "TYPE":
                            if (argument.Equals("I", StringComparison.OrdinalIgnoreCase))
                            {
                                session.Binary = true;
                                await ReplyAsync(writer, "200 Switching to Binary mode.", ct);
                            }
                            else
                            {
                                await ReplyAsync(writer, "504 Unknown type.", ct);
                            }
                            break;
                        case "PWD":
                            await ReplyAsync(writer, "257 \"/\" is current directory.", ct);
                            break;
                        case "CWD":
                        case "CDUP":
                            await ReplyAsync(writer, "250 Directory successfully changed.", ct);
                            break;
                        case "SIZE":
                            await ReplyAsync(writer, RequireAuth(session)
                                ? SizeReply(argument)
                                : "530 Please login with USER and PASS.", ct);
                            break;
                        case "REST":
                            await ReplyAsync(writer, RequireAuth(session)
                                ? RestReply(argument, session)
                                : "530 Please login with USER and PASS.", ct);
                            break;
                        case "PASV":
                            await ReplyAsync(writer, await PasvReplyAsync(session, ct), ct);
                            break;
                        case "EPSV":
                            await ReplyAsync(writer, await EpsvReplyAsync(session, ct), ct);
                            break;
                        case "RETR":
                            await HandleRetrAsync(writer, stream, session, argument, ct);
                            break;
                        case "AUTH":
                            (stream, reader, writer, var ok) = await HandleAuthTlsAsync(stream, reader, writer, session, argument, ct);
                            if (!ok)
                                return;
                            break;
                        case "PBSZ":
                            await ReplyAsync(writer, session.Encrypted
                                ? "200 PBSZ set to 0."
                                : "503 PBSZ requires a secure connection.", ct);
                            break;
                        case "PROT":
                            await ReplyAsync(writer, session.Encrypted
                                ? "200 PROT set."
                                : "503 PROT requires a secure connection.", ct);
                            break;
                        case "NOOP":
                            await ReplyAsync(writer, "200 OK", ct);
                            break;
                        case "QUIT":
                            await ReplyAsync(writer, "221 Goodbye.", ct);
                            return;
                        default:
                            await ReplyAsync(writer, "502 Command not implemented.", ct);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // Client went away; expected during drop/cancel tests.
            }
            catch (SocketException)
            {
            }
            catch (AuthenticationException)
            {
                // TLS handshake failed (e.g. client rejected the cert).
            }
            finally
            {
                try { stream.Dispose(); } catch { }
            }
        }
    }

    private sealed class SessionState
    {
        public string? UserName { get; set; }
        public bool Authenticated { get; set; }
        public bool Binary { get; set; } = true;
        public bool Encrypted { get; set; }
        public long RestartOffset { get; set; }
        public Socket? DataListener { get; set; }
    }

    private static bool RequireAuth(SessionState session) => session.Authenticated;

    private string FeatReply()
    {
        var features = new StringBuilder();
        features.Append("211-Features:\r\n");
        if (_options.Behavior != FtpServerBehavior.NoRest)
            features.Append(" REST STREAM\r\n");
        features.Append(" SIZE\r\n EPSV\r\n PASV\r\n");
        if (_options.EnableTls)
            features.Append(" AUTH TLS\r\n PBSZ\r\n PROT\r\n");
        features.Append("211 End");
        return features.ToString();
    }

    private string SizeReply(string argument)
    {
        if (argument != "/file.bin")
            return "550 File not found.";
        return $"213 {new FileInfo(_filePath).Length}";
    }

    private string RestReply(string argument, SessionState session)
    {
        if (_options.Behavior == FtpServerBehavior.NoRest)
            return "502 Command not implemented.";
        if (!long.TryParse(argument, out var offset) || offset < 0)
            return "501 Invalid restart position.";
        session.RestartOffset = offset;
        return "350 Restart position accepted.";
    }

    private async Task<string> PasvReplyAsync(SessionState session, CancellationToken ct)
    {
        CloseDataListener(session);
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        session.DataListener = listener;
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        await Task.CompletedTask;
        return $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})";
    }

    private async Task<string> EpsvReplyAsync(SessionState session, CancellationToken ct)
    {
        CloseDataListener(session);
        var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        listener.Listen(1);
        session.DataListener = listener;
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        await Task.CompletedTask;
        return $"229 Entering Extended Passive Mode (|||{port}|)";
    }

    private static void CloseDataListener(SessionState session)
    {
        try { session.DataListener?.Dispose(); } catch { }
        session.DataListener = null;
    }

    private async Task HandleRetrAsync(
        StreamWriter writer, Stream controlStream, SessionState session, string argument, CancellationToken ct)
    {
        if (!session.Authenticated)
        {
            await ReplyAsync(writer, "530 Please login with USER and PASS.", ct);
            return;
        }
        if (argument != "/file.bin")
        {
            await ReplyAsync(writer, "550 File not found.", ct);
            return;
        }
        if (session.DataListener is null)
        {
            await ReplyAsync(writer, "425 Use PASV or EPSV first.", ct);
            return;
        }

        var total = new FileInfo(_filePath).Length;
        var start = _options.Behavior == FtpServerBehavior.IgnoreRest ? 0 : session.RestartOffset;
        start = Math.Min(start, total);
        var bytesToSend = total - start;

        await ReplyAsync(writer, "150 Opening BINARY mode data connection.", ct);

        // The client connects the data connection around the time it
        // sends RETR; wait briefly for it, then give up.
        Socket? dataSocket;
        using (var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            acceptCts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                dataSocket = await session.DataListener.AcceptAsync(acceptCts.Token);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(writer, "425 Failed to establish data connection.", ct);
                return;
            }
        }
        CloseDataListener(session);

        var shouldDrop = _options.Behavior == FtpServerBehavior.DropData
            && Interlocked.Increment(ref _dropCount) <= _options.DropFirstNTransfers;
        var bytesThisTransfer = shouldDrop
            ? Math.Min(_options.DropAfterBytes, bytesToSend)
            : bytesToSend;

        using (dataSocket)
        {
            Stream dataStream = new NetworkStream(dataSocket, ownsSocket: true);
            try
            {
                if (session.Encrypted)
                {
                    var tls = new SslStream(dataStream, leaveInnerStreamOpen: false);
                    await tls.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _certificate,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                            ClientCertificateRequired = false
                        }, ct);
                    dataStream = tls;
                }

                await using var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                file.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[64 * 1024];
                var remaining = bytesThisTransfer;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await file.ReadAsync(buffer.AsMemory(0, toRead), ct);
                    if (read == 0)
                        break;
                    await dataStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    await dataStream.FlushAsync(ct);
                    remaining -= read;
                }

                if (shouldDrop)
                {
                    // FTP has no Content-Length on the data connection,
                    // so a graceful close (FIN) after partial data looks
                    // like a clean end of transfer to the client. Force
                    // a RST instead: linger time 0 makes the socket
                    // close reset the connection, so the client's next
                    // read throws instead of returning a clean EOF.
                    dataSocket.LingerState = new LingerOption(true, 0);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                try { dataStream.Dispose(); } catch { }
            }
        }

        if (shouldDrop)
        {
            // Abrupt close: the client sees a truncated body. A 426 on
            // the control connection lets the client's cleanup finish
            // promptly instead of waiting for a read timeout.
            await ReplyAsync(writer, "426 Connection closed; transfer aborted.", ct);
            return;
        }

        session.RestartOffset = 0;
        await ReplyAsync(writer, "226 Transfer complete.", ct);
    }

    private async Task<(Stream, StreamReader, StreamWriter, bool)> HandleAuthTlsAsync(
        Stream stream, StreamReader reader, StreamWriter writer, SessionState session,
        string argument, CancellationToken ct)
    {
        if (!_options.EnableTls)
        {
            await ReplyAsync(writer, "502 Command not implemented.", ct);
            return (stream, reader, writer, true);
        }
        if (!argument.Equals("TLS", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(writer, "502 Command not implemented.", ct);
            return (stream, reader, writer, true);
        }
        if (session.Encrypted)
        {
            await ReplyAsync(writer, "503 Already encrypted.", ct);
            return (stream, reader, writer, true);
        }

        await ReplyAsync(writer, "234 Proceed with negotiation.", ct);
        // The client sends nothing else until the handshake finishes,
        // so the plain StreamReader cannot have buffered bytes past
        // the AUTH line.
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ClientCertificateRequired = false
            }, ct);
        session.Encrypted = true;
        var newReader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
        var newWriter = new StreamWriter(tls, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };
        return (tls, newReader, newWriter, true);
    }

    private static (string Verb, string Argument) SplitCommand(string line)
    {
        var space = line.IndexOf(' ');
        if (space < 0)
            return (line.Trim().ToUpperInvariant(), string.Empty);
        return (line[..space].Trim().ToUpperInvariant(), line[(space + 1)..].Trim());
    }

    private static async Task WriteLineAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\r\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task ReplyAsync(StreamWriter writer, string text, CancellationToken ct)
    {
        // FTP lines end with CRLF. StreamWriter.WriteLineAsync would
        // use Environment.NewLine ("\n" on Linux), so the terminator is
        // always written explicitly. Multi-line replies (FEAT) already
        // contain \r\n separators and get exactly one final terminator.
        await writer.WriteAsync(text + "\r\n");
        await writer.FlushAsync();
    }

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.IPv6Loopback);
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        var fresh = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Same Schannel/ephemeral-key reason as TestCertificates: the
        // freshly created certificate cannot be used for SslStream
        // server authentication on Windows without a PKCS#12 round-trip.
        return TestCertificates.ToServerCertificate(fresh);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var listener in _listeners)
        {
            try { listener.Close(); } catch { }
        }
        try { await Task.WhenAll(_acceptLoops); } catch { }
        _cts.Dispose();
        _certificate?.Dispose();
        if (_options.UnixSocketPath is not null)
        {
            try { File.Delete(_options.UnixSocketPath); } catch { }
        }
    }
}

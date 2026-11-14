using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AiDownloadManager.TestServer;

/// <summary>Fault-injection modes for the deterministic test server.</summary>
public enum TestServerBehavior
{
    /// <summary>Correct Range support (206 + exact Content-Range).</summary>
    Normal,
    /// <summary>Ignores Range headers entirely; always answers 200 with the full body.</summary>
    IgnoreRange,
    /// <summary>Answers a Range request with 200 and the full body (wrong status).</summary>
    InvalidRange,
    /// <summary>Answers 206 but with a Content-Range that does not match the request.</summary>
    InconsistentContentRange,
    /// <summary>Closes the connection after N bytes (first K connections only, then behaves normally).</summary>
    DropConnection
}

/// <summary>
/// Deterministic local HTTP/1.1 test server options. Dropping is
/// orthogonal to the behavior: set <see cref="DropAfterBytes"/> and
/// <see cref="DropFirstNConnections"/> to drop the first K connections
/// after N bytes, whatever the behavior is. This lets a no-Range server
/// (fallback path) drop connections too.
/// </summary>
public sealed record TestServerOptions(
    long FileSizeBytes,
    TestServerBehavior Behavior = TestServerBehavior.Normal,
    int ThrottleBytesPerSecond = 0,
    long DropAfterBytes = 0,
    int DropFirstNConnections = 1,
    int Seed = 42,
    /// <summary>
    /// When set, the server listens on a Unix domain socket at this path
    /// instead of TCP loopback. Use this in sandboxes where .NET TCP is
    /// blocked (the engine is transport-agnostic, so the same code paths
    /// are exercised). The HTTP handling is identical either way.
    /// </summary>
    string? UnixSocketPath = null,
    /// <summary>
    /// When set, the server speaks TLS (HTTPS) using this certificate.
    /// The certificate is presented to every client; clients in tests
    /// either pin its thumbprint or use default validation (which
    /// correctly rejects the self-signed chain). Never set in production
    /// code — this server is test-only.
    /// </summary>
    X509Certificate2? ServerCertificate = null,
    /// <summary>
    /// When set, requests to this path (e.g. "/redirect") are answered
    /// with 302 Found pointing at /file.bin, so redirect following can
    /// be tested. All other paths serve the file normally.
    /// </summary>
    string? RedirectPath = null);

/// <summary>
/// Deterministic local HTTP/1.1 test server on a raw <see cref="Socket"/>.
/// Listens on TCP loopback (127.0.0.1, ephemeral port) by default, or on
/// a Unix domain socket when <see cref="TestServerOptions.UnixSocketPath"/>
/// is set. Serves one generated file and supports Range requests, HEAD,
/// throttling, and fault injection. Hand-rolled on purpose: a minimal
/// server gives exact control over malformed responses, which a
/// framework server would refuse to produce. Never used for anything
/// except tests.
/// </summary>
public sealed class DeterministicTestServer : IAsyncDisposable
{
    private readonly string _filePath;
    private readonly TestServerOptions _options;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _dropCount;

    public DeterministicTestServer(string filePath, TestServerOptions options)
    {
        _filePath = filePath;
        _options = options;
        if (options.UnixSocketPath is not null)
        {
            try { File.Delete(options.UnixSocketPath); } catch { }
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(options.UnixSocketPath));
        }
        else
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }
    }

    public string BaseUrl { get; private set; } = string.Empty;
    public string FileUrl => BaseUrl + "file.bin";

    /// <summary>
    /// Returns an <see cref="HttpClient"/> that reaches this server:
    /// over the Unix socket when one is configured (via a
    /// <see cref="SocketsHttpHandler.ConnectCallback"/>), or a plain
    /// client for TCP loopback. When <paramref name="trustedCertificate"/>
    /// is supplied, the client pins that exact certificate thumbprint
    /// (test-only: the test server's certificate is self-signed, so
    /// default validation would reject it). When it is not supplied and
    /// the server uses TLS, default system validation applies — which
    /// correctly rejects the self-signed chain.
    /// </summary>
    public HttpClient CreateHttpClient(X509Certificate2? trustedCertificate = null)
    {
        SocketsHttpHandler handler;
        if (_options.UnixSocketPath is null)
        {
            handler = new SocketsHttpHandler();
        }
        else
        {
            var socketPath = _options.UnixSocketPath;
            handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
        }
        if (trustedCertificate is not null)
        {
            var thumbprint = trustedCertificate.Thumbprint;
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                // X509Certificate (not X509Certificate2) has no Thumbprint
                // property; GetCertHashString() returns the same SHA-1 hash.
                RemoteCertificateValidationCallback = (message, cert, chain, errors) =>
                    cert is not null &&
                    string.Equals(cert.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase)
            };
        }
        return new HttpClient(handler);
    }

    public async Task StartAsync()
    {
        _listener.Listen(backlog: 128);
        var scheme = _options.ServerCertificate is not null ? "https" : "http";
        BaseUrl = _options.UnixSocketPath is not null
            ? $"{scheme}://localhost/"
            : $"{scheme}://127.0.0.1:{((IPEndPoint)_listener.LocalEndPoint!).Port}/";
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await _listener.AcceptAsync(ct);
                _ = HandleConnectionAsync(socket, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (ObjectDisposedException)
        {
            // Listener stopped.
        }
        catch (SocketException)
        {
            // Listener stopped.
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken ct)
    {
        using (socket)
        {
            Stream stream = new NetworkStream(socket, ownsSocket: true);
            try
            {
                if (_options.ServerCertificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _options.ServerCertificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, ct);
                    stream = ssl;
                }
                using (stream)
                {
                    var request = await ReadRequestAsync(stream, ct);
                    if (request is null)
                        return;
                    await ServeAsync(stream, socket, request, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                // Client rejected the certificate or the handshake failed;
                // expected in certificate-validation tests.
            }
            catch (IOException)
            {
                // Client went away mid-response; expected during drop/cancel tests.
            }
            catch (SocketException)
            {
                // Client went away mid-response.
            }
        }
    }

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers);

    private static async Task<HttpRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(requestLine))
            return null;
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
            return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrWhiteSpace(line = await reader.ReadLineAsync(ct)))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return new HttpRequest(parts[0], parts[1], headers);
    }

    private async Task ServeAsync(Stream stream, Socket socket, HttpRequest request, CancellationToken ct)
    {
        var fileInfo = new FileInfo(_filePath);
        var total = fileInfo.Length;
        var isHead = request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        if (_options.RedirectPath is not null &&
            request.Path.Equals(_options.RedirectPath, StringComparison.OrdinalIgnoreCase))
        {
            var redirect = "HTTP/1.1 302 Found\r\nLocation: /file.bin\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(redirect), ct);
            await stream.FlushAsync(ct);
            return;
        }

        long rangeStart = 0, rangeEnd = total - 1;
        var hasRange = request.Headers.TryGetValue("Range", out var rangeHeader)
            && TryParseRange(rangeHeader, total, out rangeStart, out rangeEnd);

        var shouldDrop = _options.DropAfterBytes > 0
            && Interlocked.Increment(ref _dropCount) <= _options.DropFirstNConnections;

        // Decide the response.
        int status;
        string statusText;
        long bodyStart, bodyEnd;
        string? contentRange = null;

        if (_options.Behavior == TestServerBehavior.IgnoreRange)
        {
            // Ignores Range headers entirely; always answers 200 with the full body.
            status = 200; statusText = "OK";
            bodyStart = 0; bodyEnd = total - 1;
        }
        else if (_options.Behavior == TestServerBehavior.InvalidRange
            && hasRange && !(rangeStart == 0 && rangeEnd == 0))
        {
            // Answers a real Range request with 200 and the full body.
            // The 1-byte probe (bytes=0-0) is answered correctly, so this
            // models a server that passes the probe but mishandles real
            // range downloads — the transport must detect it and fail
            // safely instead of writing the wrong bytes.
            status = 200; statusText = "OK";
            bodyStart = 0; bodyEnd = total - 1;
        }
        else if (_options.Behavior == TestServerBehavior.InconsistentContentRange && hasRange)
        {
            status = 206; statusText = "Partial Content";
            bodyStart = rangeStart; bodyEnd = rangeEnd;
            // Lie about the range: claim one byte more than requested.
            contentRange = $"bytes {rangeStart}-{rangeEnd + 1}/{total}";
        }
        else if (hasRange)
        {
            status = 206; statusText = "Partial Content";
            bodyStart = rangeStart; bodyEnd = rangeEnd;
            contentRange = $"bytes {rangeStart}-{rangeEnd}/{total}";
        }
        else
        {
            status = 200; statusText = "OK";
            bodyStart = 0; bodyEnd = total - 1;
        }

        var bodyLength = bodyEnd - bodyStart + 1;
        var header = new StringBuilder();
        header.Append($"HTTP/1.1 {status} {statusText}\r\n");
        header.Append("Accept-Ranges: bytes\r\n");
        header.Append("Content-Type: application/octet-stream\r\n");
        header.Append($"Content-Length: {bodyLength}\r\n");
        if (contentRange is not null)
            header.Append($"Content-Range: {contentRange}\r\n");
        header.Append("Connection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes, ct);

        if (isHead)
            return;

        var bytesToWrite = shouldDrop
            ? Math.Min(_options.DropAfterBytes, bodyLength)
            : bodyLength;

        await using var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        file.Seek(bodyStart, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var remaining = bytesToWrite;
        var throttleDelay = _options.ThrottleBytesPerSecond > 0
            ? TimeSpan.FromSeconds((double)buffer.Length / _options.ThrottleBytesPerSecond)
            : TimeSpan.Zero;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await file.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0)
                break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            await stream.FlushAsync(ct);
            remaining -= read;
            if (throttleDelay > TimeSpan.Zero)
                await Task.Delay(throttleDelay, ct);
        }

        if (shouldDrop)
        {
            // Abrupt close: the client sees a truncated body.
            AbruptClose(socket);
        }
    }

    private static void AbruptClose(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // Already gone.
        }
    }

    private static bool TryParseRange(string value, long total, out long start, out long end)
    {
        start = 0; end = total - 1;
        // Supported forms: bytes=start-end and bytes=start-
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;
        var spec = value["bytes=".Length..];
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;
        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];
        if (string.IsNullOrWhiteSpace(startText))
            return false; // suffix ranges are not supported by this server
        if (!long.TryParse(startText, out start) || start < 0 || start >= total)
            return false;
        if (string.IsNullOrWhiteSpace(endText))
        {
            end = total - 1;
            return true;
        }
        if (!long.TryParse(endText, out end) || end < start)
            return false;
        end = Math.Min(end, total - 1);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }
        _cts.Dispose();
        if (_options.UnixSocketPath is not null)
        {
            try { File.Delete(_options.UnixSocketPath); } catch { }
        }
    }
}

/// <summary>
/// Generates deterministic test data with a seeded xorshift generator,
/// so the same seed always produces the same bytes (and the same
/// SHA-256) on any machine. Returns the SHA-256 of the generated file.
/// </summary>
public static class TestDataGenerator
{
    public static string Generate(string path, long sizeBytes, int seed)
    {
        using var sha = SHA256.Create();
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var state = (ulong)(seed == 0 ? 1 : seed);
        state |= 1UL; // never zero
        var block = new byte[1024 * 1024];
        var remaining = sizeBytes;
        while (remaining > 0)
        {
            var toWrite = (int)Math.Min(block.Length, remaining);
            Fill(block.AsSpan(0, toWrite), ref state);
            file.Write(block, 0, toWrite);
            sha.TransformBlock(block, 0, toWrite, null, 0);
            remaining -= toWrite;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void Fill(Span<byte> span, ref ulong state)
    {
        for (var i = 0; i + 8 <= span.Length; i += 8)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            BitConverter.TryWriteBytes(span.Slice(i, 8), state);
        }
        if (span.Length % 8 != 0)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            var tail = BitConverter.GetBytes(state);
            tail.AsSpan(0, span.Length % 8).CopyTo(span.Slice(span.Length - span.Length % 8));
        }
    }

    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}

using System.Net;
using System.Net.Sockets;
using AiDownloadManager.Core;
using FluentFTP;
using FluentFTP.Client.BaseClient;
using FluentFTP.Exceptions;

namespace AiDownloadManager.Protocols;

/// <summary>
/// Options for <see cref="FtpTransport"/>.
/// </summary>
public sealed class FtpTransportOptions
{
    /// <summary>
    /// Credentials used for every FTP login. When null, credentials are
    /// taken from the URL userinfo (<c>ftp://user:pass@host/path</c>);
    /// when the URL has no userinfo either, anonymous login is used
    /// (user <c>anonymous</c>). Explicit credentials take precedence
    /// over URL userinfo. Credentials are never written to any log:
    /// this transport has no logger and its exception messages never
    /// contain the userinfo part of a URL.
    /// </summary>
    public NetworkCredential? Credentials { get; init; }

    /// <summary>
    /// Encryption for plain <c>ftp://</c> URLs. A <c>ftps://</c> URL
    /// always forces <see cref="FtpEncryptionMode.Explicit"/>; this
    /// setting only applies to <c>ftp://</c> URLs. Default
    /// <see cref="FtpEncryptionMode.None"/>.
    /// </summary>
    public FtpEncryptionMode Encryption { get; init; } = FtpEncryptionMode.None;

    /// <summary>Timeout for connect, read and data-connection operations. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// TEST ONLY. When set, the server certificate is trusted if and
    /// only if its SHA-1 thumbprint equals this value (case-insensitive,
    /// dashes ignored), regardless of chain errors. This exists so the
    /// local deterministic test server can present a self-signed test
    /// certificate. It must never be set in production: without it, the
    /// certificate is validated against the system trust store and any
    /// validation error rejects the connection. There is no accept-all
    /// mode anywhere in this transport.
    /// </summary>
    public string? ExpectedCertificateThumbprint { get; init; }

    /// <summary>Encrypt FTP data connections when using FTPS. Default true.</summary>
    public bool DataConnectionEncryption { get; init; } = true;
}

/// <summary>
/// FTP transport over FluentFTP (<see cref="ITransport"/>).
///
/// Thread-safety: the engine calls <see cref="DownloadRangeAsync"/>
/// concurrently from several workers. FluentFTP's
/// <c>AsyncFtpClient</c> is a single control connection and is not
/// safe for concurrent transfers, so every operation creates, connects
/// and disposes its own client. That costs one login per range, which
/// is acceptable for Phase 1; connection reuse is future work, not a
/// correctness concern.
///
/// Range validation: unlike HTTP, FTP has no Content-Range header and
/// RETR has no stop position — a RETR always sends from the restart
/// offset to the end of the file. The transport therefore reads
/// exactly the requested number of bytes and stops; over-delivery is
/// normal FTP behaviour, not an error. A server that ignores REST
/// cannot be detected at this layer (the bytes it returns have the
/// right length but the wrong content); that fault is caught by the
/// engine's final SHA-256 verification, which refuses to promote a
/// mismatched file. See the engine-level IgnoreRest test.
/// </summary>
public sealed class FtpTransport : ITransport
{
    private readonly FtpTransportOptions _options;
    private readonly string? _pin;

    public FtpTransport(FtpTransportOptions? options = null)
    {
        _options = options ?? new FtpTransportOptions();
        _pin = _options.ExpectedCertificateThumbprint is null
            ? null
            : NormalizeThumbprint(_options.ExpectedCertificateThumbprint);
    }

    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        var parsed = ParseUrl(url);
        var client = await ConnectAsync(parsed, cancellationToken);
        try
        {
            await RequireOkAsync(client, "TYPE I", cancellationToken);

            // SIZE: 213 <size> on success, 550 when the file is missing.
            var sizeReply = await client.Execute($"SIZE {parsed.Path}", cancellationToken);
            long? total = null;
            if (sizeReply.Code == "213" && long.TryParse(sizeReply.Message.Trim(), out var size))
            {
                total = size;
            }
            else if (sizeReply.Code is "550" or "553")
            {
                throw new TransportException(TransportFailureKind.NotFoundOrForbidden,
                    $"File not found on FTP server: {parsed.Redacted}");
            }
            // Any other SIZE reply (e.g. 500/502: SIZE unsupported) leaves
            // the size unknown; the file's existence is then established
            // by the download itself, which fails with 550 at RETR time.

            // REST is probed for real (REST 0), not trusted from FEAT:
            // a server may advertise REST and still reject it.
            var restReply = await client.Execute("REST 0", cancellationToken);
            var supportsRange = restReply.Code == "350";

            var fileName = Path.GetFileName(parsed.Path);
            return new ProbeResult(total, supportsRange, null,
                string.IsNullOrEmpty(fileName) ? null : fileName);
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task DownloadRangeAsync(
        string url, long start, long end,
        Func<ReadOnlyMemory<byte>, long, Task> onChunk,
        CancellationToken cancellationToken)
    {
        var expected = end - start + 1;
        if (expected <= 0)
            throw new ArgumentOutOfRangeException(nameof(end), "Range end must be >= start.");

        var parsed = ParseUrl(url);
        var client = await ConnectAsync(parsed, cancellationToken);
        try
        {
            await RequireOkAsync(client, "TYPE I", cancellationToken);
            await using var stream = await OpenReadAsync(client, parsed, start, expected, cancellationToken);
            await ReadExactAsync(stream, start, expected, onChunk, cancellationToken, parsed.Redacted);
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task DownloadToEndAsync(
        string url,
        Func<ReadOnlyMemory<byte>, long, Task> onChunk,
        CancellationToken cancellationToken)
    {
        var parsed = ParseUrl(url);
        var client = await ConnectAsync(parsed, cancellationToken);
        try
        {
            await RequireOkAsync(client, "TYPE I", cancellationToken);

            // FTP data connections carry no length framing: the end of
            // a transfer is signalled only by the server closing the
            // connection, so a server that closes early (dropped
            // connection) otherwise looks like a clean end-of-data and
            // a truncated download is returned as success. Ask for the
            // size first when the server supports SIZE, and verify the
            // byte count: a short transfer is Transient, so the engine
            // retries it. When SIZE is unsupported the size stays
            // unknown and the download proceeds as before (the
            // engine's SHA-256 check remains the safety net).
            var expected = await TryGetSizeAsync(client, parsed, cancellationToken);
            await using var stream = await OpenReadAsync(client, parsed, 0, 0, cancellationToken);
            var bytesRead = await ReadToEndAsync(stream, onChunk, cancellationToken);
            if (expected is not null && bytesRead != expected.Value)
            {
                throw new TransportException(TransportFailureKind.Transient,
                    $"FTP transfer truncated for {parsed.Redacted}: got {bytesRead} of {expected.Value} bytes");
            }
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    /// <summary>
    /// Best-effort SIZE: returns the file size when the server answers
    /// 213, or null when SIZE is unsupported or fails. Existence is
    /// established by RETR itself, so a SIZE failure here is never
    /// fatal and never masks the real download error.
    /// </summary>
    private static async Task<long?> TryGetSizeAsync(
        AsyncFtpClient client, ParsedUrl parsed, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await ExecuteAsync(client, $"SIZE {parsed.Path}", cancellationToken);
            if (reply.Code == "213" && long.TryParse(reply.Message.Trim(), out var size) && size >= 0)
                return size;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // ---- connection setup -------------------------------------------------

    private sealed record ParsedUrl(
        string Host, int Port, string Path, string User, string Password,
        FtpEncryptionMode Encryption, string Redacted);

    private ParsedUrl ParseUrl(string url)
    {
        Uri uri;
        try
        {
            uri = new Uri(url);
        }
        catch (UriFormatException ex)
        {
            throw new TransportException(TransportFailureKind.Unreachable, $"Malformed FTP URL: {url}", ex);
        }

        if (uri.Scheme is not ("ftp" or "ftps"))
            throw new TransportException(TransportFailureKind.Unreachable,
                $"Unsupported URL scheme '{uri.Scheme}' (expected ftp:// or ftps://)");

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (string.IsNullOrEmpty(path) || path == "/")
            throw new TransportException(TransportFailureKind.Unreachable,
                $"FTP URL has no file path: {Redact(uri)}");

        string user, password;
        if (_options.Credentials is not null)
        {
            user = _options.Credentials.UserName;
            password = _options.Credentials.Password;
        }
        else if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var userInfo = Uri.UnescapeDataString(uri.UserInfo);
            var colon = userInfo.IndexOf(':');
            user = colon < 0 ? userInfo : userInfo[..colon];
            password = colon < 0 ? string.Empty : userInfo[(colon + 1)..];
        }
        else
        {
            user = "anonymous";
            password = "anonymous";
        }

        var encryption = uri.Scheme == "ftps"
            ? FtpEncryptionMode.Explicit
            : _options.Encryption;

        return new ParsedUrl(
            uri.Host,
            uri.Port == -1 ? 21 : uri.Port,
            path, user, password, encryption,
            Redact(uri));
    }

    private static string Redact(Uri uri)
    {
        // Never include userinfo (which may hold a password) in messages.
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri.ToString();
    }

    private async Task<AsyncFtpClient> ConnectAsync(ParsedUrl parsed, CancellationToken cancellationToken)
    {
        // Resolve first: a DNS failure is Unreachable, not Transient.
        // FluentFTP would report it as a generic "Failed to connect".
        if (!IPAddress.TryParse(parsed.Host, out _))
        {
            try
            {
                await Dns.GetHostAddressesAsync(parsed.Host, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TransportException(TransportFailureKind.Unreachable,
                    $"Cannot resolve FTP host '{parsed.Host}'", ex);
            }
        }

        var client = new AsyncFtpClient(parsed.Host, parsed.User, parsed.Password, parsed.Port);
        client.Config.EncryptionMode = parsed.Encryption;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.DataConnectionEncryption = _options.DataConnectionEncryption;
        client.Config.ConnectTimeout = (int)_options.Timeout.TotalMilliseconds;
        client.Config.ReadTimeout = (int)_options.Timeout.TotalMilliseconds;
        client.Config.DataConnectionConnectTimeout = (int)_options.Timeout.TotalMilliseconds;
        client.Config.DataConnectionReadTimeout = (int)_options.Timeout.TotalMilliseconds;
        if (_pin is not null)
        {
            // The pin replaces chain validation for the test server's
            // self-signed certificate; revocation checks are meaningless
            // for it and are disabled only in this pinned case.
            client.Config.ValidateCertificateRevocation = false;
        }
        if (parsed.Encryption != FtpEncryptionMode.None)
        {
            client.ValidateCertificate += OnValidateCertificate;
        }

        try
        {
            await client.Connect(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw;
        }
        catch (FtpAuthenticationException ex)
        {
            // Wrong credentials: permanent, must not be retried as if
            // the network had glitched. NotFoundOrForbidden is the
            // closest permanent kind (a login failure is a refusal).
            client.Dispose();
            throw new TransportException(TransportFailureKind.NotFoundOrForbidden,
                $"FTP authentication failed for {parsed.Redacted}", ex);
        }
        catch (FtpCommandException ex) when (ex.CompletionCode is "530" or "531" or "532" or "533")
        {
            client.Dispose();
            throw new TransportException(TransportFailureKind.NotFoundOrForbidden,
                $"FTP authentication failed for {parsed.Redacted}", ex);
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException or TimeoutException
            or System.Security.Authentication.AuthenticationException)
        {
            client.Dispose();
            // A TLS handshake failure (bad/untrusted certificate) is not
            // a network glitch, but retrying it is harmless: the engine
            // retries Transient a bounded number of times and then fails
            // with this message, which names the real cause.
            throw new TransportException(TransportFailureKind.Transient, $"Cannot connect to {parsed.Redacted}: {ex.Message}", ex);
        }

        return client;
    }

    private void OnValidateCertificate(BaseFtpClient control, FtpSslValidationEventArgs e)
    {
        if (_pin is not null)
        {
            // Test-only pinning: trust exactly this certificate.
            e.Accept = e.Certificate is not null &&
                string.Equals(NormalizeThumbprint(e.Certificate.GetCertHashString()), _pin,
                    StringComparison.OrdinalIgnoreCase);
            return;
        }

        // Production: the system trust store decides. Any policy error
        // (untrusted root, name mismatch, expired) rejects the server.
        // There is no accept-all path.
        e.Accept = e.PolicyErrors == System.Net.Security.SslPolicyErrors.None;
    }

    private static string NormalizeThumbprint(string thumbprint) =>
        thumbprint.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();

    private static async Task DisconnectQuietlyAsync(AsyncFtpClient client)
    {
        try { await client.Disconnect(CancellationToken.None); } catch { }
        try { client.Dispose(); } catch { }
    }

    // ---- commands ---------------------------------------------------------

    /// <summary>
    /// Sends a command that must succeed (2xx reply); anything else is
    /// a transient server failure. Used for TYPE I, which must be binary
    /// before any transfer.
    /// </summary>
    private static async Task RequireOkAsync(
        AsyncFtpClient client, string command, CancellationToken cancellationToken)
    {
        var reply = await ExecuteAsync(client, command, cancellationToken);
        if (reply.Code is not ("200" or "250"))
            throw new TransportException(TransportFailureKind.Transient,
                $"FTP command '{command}' failed with reply {reply.Code} {reply.Message}");
    }

    private static async Task<FtpReply> ExecuteAsync(
        AsyncFtpClient client, string command, CancellationToken cancellationToken)
    {
        try
        {
            return await client.Execute(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FtpCommandException ex)
        {
            // FluentFTP surfaces some failed commands as exceptions
            // instead of replies (notably 550 from SIZE on some builds).
            // Convert back to a reply so callers can decide by code.
            return new FtpReply { Code = ex.CompletionCode ?? string.Empty, Message = ex.Message };
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException or TimeoutException)
        {
            throw new TransportException(TransportFailureKind.Transient,
                $"FTP command '{command.Split(' ')[0]}' failed: {ex.Message}", ex);
        }
    }

    private static async Task<Stream> OpenReadAsync(
        AsyncFtpClient client, ParsedUrl parsed, long restart, long expectedLength,
        CancellationToken cancellationToken)
    {
        try
        {
            // checkIfFileExists=false: existence is established by RETR
            // itself (550 -> NotFoundOrForbidden below).
            return await client.OpenRead(parsed.Path, FtpDataType.Binary, restart, expectedLength, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FtpCommandException ex)
        {
            throw ex.CompletionCode switch
            {
                "550" or "553" => new TransportException(TransportFailureKind.NotFoundOrForbidden,
                    $"File not found on FTP server: {parsed.Redacted}", ex),
                var code when code.StartsWith("4", StringComparison.Ordinal) => new TransportException(
                    TransportFailureKind.Transient, $"FTP server refused the transfer ({code}): {ex.Message}", ex),
                var code => new TransportException(TransportFailureKind.InvalidRangeResponse,
                    $"FTP server rejected the range request ({code}): {ex.Message}", ex),
            };
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException or TimeoutException
            or FtpMissingSocketException)
        {
            throw new TransportException(TransportFailureKind.Transient,
                $"FTP transfer failed for {parsed.Redacted}: {ex.Message}", ex);
        }
    }

    // ---- body reading -----------------------------------------------------

    private static async Task ReadExactAsync(
        Stream stream, long startOffset, long expected,
        Func<ReadOnlyMemory<byte>, long, Task> onChunk,
        CancellationToken cancellationToken, string redactedUrl)
    {
        var buffer = new byte[64 * 1024];
        var offset = startOffset;
        var remaining = expected;
        try
        {
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (read == 0)
                {
                    // The server closed the transfer early: truncated
                    // body. Transient, so the engine retries the rest.
                    throw new TransportException(TransportFailureKind.Transient,
                        $"FTP transfer truncated for {redactedUrl}: got {expected - remaining} of {expected} bytes");
                }
                await onChunk(buffer.AsMemory(0, read), offset);
                offset += read;
                remaining -= read;
            }

            // Exactly the requested bytes were read. The server will
            // keep sending to end-of-file (RETR has no stop position);
            // the stream is closed by the caller, which discards the
            // remainder. A server that ignored REST cannot be detected
            // here — see the class remarks; the engine hash check is
            // the safety net for that fault.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TransportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or FtpException)
        {
            throw new TransportException(TransportFailureKind.Transient,
                $"FTP body read failed for {redactedUrl}: {ex.Message}", ex);
        }
    }

    /// <summary>Reads the stream to end, invoking the chunk callback; returns the total bytes read.</summary>
    private static async Task<long> ReadToEndAsync(
        Stream stream,
        Func<ReadOnlyMemory<byte>, long, Task> onChunk,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var offset = 0L;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await onChunk(buffer.AsMemory(0, read), offset);
                offset += read;
            }
            return offset;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or FtpException)
        {
            throw new TransportException(TransportFailureKind.Transient,
                $"FTP body read failed: {ex.Message}", ex);
        }
    }
}

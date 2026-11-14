using System.Net;
using System.Net.Http.Headers;
using AiDownloadManager.Core;

namespace AiDownloadManager.Protocols;

/// <summary>
/// HTTP/HTTPS transport over <see cref="HttpClient"/>.
/// Range support is verified with a real Range probe (bytes=0-0), and
/// every Range response is validated against the requested range, so a
/// server that lies about Range support is detected instead of silently
/// producing a corrupt file.
/// </summary>
public sealed class HttpTransport : ITransport, IDisposable
{
    private readonly HttpClient _client;

    /// <summary>
    /// Creates the transport. When <paramref name="client"/> is supplied,
    /// it is owned by the caller and is left untouched (no Timeout or
    /// header changes: mutating a client that has already sent requests
    /// throws). When null, a client is created and configured here.
    /// </summary>
    public HttpTransport(HttpClient? client = null, TimeSpan? timeout = null)
    {
        if (client is null)
        {
            _client = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 64
            });
            _client.Timeout = timeout ?? TimeSpan.FromMinutes(30);
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("AiDownloadManager/0.1 (+https://github.com/)");
        }
        else
        {
            _client = client;
        }
    }

    /// <summary>
    /// Maps a request failure to a <see cref="TransportException"/>. A TLS
    /// certificate validation failure is a <em>security</em> failure
    /// (<see cref="TransportFailureKind.CertificateError"/>): it is never
    /// retried and must never be bypassed. Everything else that is not a
    /// 404/403 is transient (the engine retries it).
    /// </summary>
    private static TransportException MapRequestException(Exception ex, string context)
    {
        if (ex is System.Security.Authentication.AuthenticationException authEx)
            return new TransportException(TransportFailureKind.CertificateError,
                $"TLS certificate validation failed for {context}: {authEx.Message}", ex);
        if (ex is HttpRequestException httpEx && httpEx.InnerException is System.Security.Authentication.AuthenticationException innerAuth)
            return new TransportException(TransportFailureKind.CertificateError,
                $"TLS certificate validation failed for {context}: {innerAuth.Message}", ex);
        if (ex is HttpRequestException httpRequestEx && httpRequestEx.InnerException is System.Net.Sockets.SocketException)
            return new TransportException(TransportFailureKind.Unreachable, $"Cannot reach {context}: {httpRequestEx.Message}", ex);
        if (ex is HttpRequestException httpRequestEx2)
            return new TransportException(TransportFailureKind.Transient, $"Request failed: {httpRequestEx2.Message}", ex);
        if (ex is TaskCanceledException)
            return new TransportException(TransportFailureKind.Transient, "Request timed out", ex);
        return new TransportException(TransportFailureKind.Transient, $"Request failed: {ex.Message}", ex);
    }

    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw MapRequestException(ex, url);
        }
        catch (System.Security.Authentication.AuthenticationException ex)
        {
            throw MapRequestException(ex, url);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransportException(TransportFailureKind.Transient, $"Timed out probing {url}", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                throw new TransportException(TransportFailureKind.NotFoundOrForbidden,
                    $"Server returned {(int)response.StatusCode} for {url}");

            if (!response.IsSuccessStatusCode)
                throw new TransportException(TransportFailureKind.Transient,
                    $"Server returned {(int)response.StatusCode} ({response.StatusCode}) for {url}");

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var contentRange = response.Content.Headers.ContentRange;
                var total = contentRange?.Length;
                if (total is null)
                    throw new TransportException(TransportFailureKind.InvalidRangeResponse,
                        "Server returned 206 without a Content-Range total");
                return new ProbeResult(total, true, response.Content.Headers.ContentType?.MediaType, null);
            }

            // 200 to a Range probe: the server ignored the Range header.
            return new ProbeResult(response.Content.Headers.ContentLength, false,
                response.Content.Headers.ContentType?.MediaType, null);
        }
    }

    public async Task DownloadRangeAsync(
        string url, long start, long end,
        Func<ReadOnlyMemory<byte>, long, Task> onChunk,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw MapRequestException(ex, url);
        }
        catch (System.Security.Authentication.AuthenticationException ex)
        {
            throw MapRequestException(ex, url);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransportException(TransportFailureKind.Transient, "Request timed out", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                throw new TransportException(TransportFailureKind.NotFoundOrForbidden,
                    $"Server returned {(int)response.StatusCode}");
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                throw new TransportException(TransportFailureKind.InvalidRangeResponse,
                    "Server returned 416 for a valid range request");
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new TransportException(TransportFailureKind.InvalidRangeResponse,
                    $"Expected 206 Partial Content, got {(int)response.StatusCode}");

            var contentRange = response.Content.Headers.ContentRange;
            if (contentRange is null || contentRange.From != start || contentRange.To != end)
                throw new TransportException(TransportFailureKind.InvalidRangeResponse,
                    $"Content-Range {contentRange} does not match requested range {start}-{end}");
            // The total length in Content-Range is not validated here: the
            // engine checks the final file length against the probe total,
            // which catches any total that affects the downloaded content.

            // A failure while reading the body (dropped connection, truncated
            // response) is transient: the engine retries the remaining bytes.
            try
            {
                await StreamBodyAsync(response, start, onChunk, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new TransportException(TransportFailureKind.Transient, $"Body read failed: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                throw new TransportException(TransportFailureKind.Transient, $"Body read failed: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TransportException(TransportFailureKind.Transient, "Body read timed out", ex);
            }
        }
    }

    public async Task DownloadToEndAsync(
        string url,
        Func<ReadOnlyMemory<byte>, long, Task> onChunk,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw MapRequestException(ex, url);
        }
        catch (System.Security.Authentication.AuthenticationException ex)
        {
            throw MapRequestException(ex, url);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransportException(TransportFailureKind.Transient, "Request timed out", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                throw new TransportException(TransportFailureKind.NotFoundOrForbidden,
                    $"Server returned {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                throw new TransportException(TransportFailureKind.Transient,
                    $"Server returned {(int)response.StatusCode} ({response.StatusCode})");

            try
            {
                await StreamBodyAsync(response, 0, onChunk, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new TransportException(TransportFailureKind.Transient, $"Body read failed: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                throw new TransportException(TransportFailureKind.Transient, $"Body read failed: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TransportException(TransportFailureKind.Transient, "Body read timed out", ex);
            }
        }
    }

    private static async Task StreamBodyAsync(
        HttpResponseMessage response, long startOffset,
        Func<ReadOnlyMemory<byte>, long, Task> onChunk,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        var offset = startOffset;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await onChunk(buffer.AsMemory(0, read), offset);
            offset += read;
        }
    }

    public void Dispose() => _client.Dispose();
}

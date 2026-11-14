namespace AiDownloadManager.Core;

public enum TransportFailureKind
{
    /// <summary>Transient network/IO failure; retry may succeed.</summary>
    Transient,
    /// <summary>The resource does not exist (HTTP 404) or is forbidden (403).</summary>
    NotFoundOrForbidden,
    /// <summary>The server answered a Range request incorrectly.</summary>
    InvalidRangeResponse,
    /// <summary>The server is unreachable or the URL is malformed.</summary>
    Unreachable,
    /// <summary>
    /// TLS certificate validation failed (untrusted, expired, or
    /// wrong-host certificate). Permanent: never retried, and never
    /// bypassed — a certificate error is a security failure, not a
    /// network glitch.
    /// </summary>
    CertificateError
}

public sealed class TransportException : Exception
{
    public TransportFailureKind Kind { get; }

    public TransportException(TransportFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}

public sealed record ProbeResult(
    long? TotalBytes,
    bool SupportsRange,
    string? ContentType,
    string? FileName);

/// <summary>
/// A single protocol transport. Implementations must be thread-safe:
/// the engine calls <see cref="DownloadRangeAsync"/> concurrently from
/// several workers.
/// </summary>
public interface ITransport
{
    /// <summary>
    /// Discovers the total length and Range support without downloading
    /// the body. Implementations should use a Range probe (bytes=0-0) so
    /// the answer reflects what Range downloads will actually do.
    /// </summary>
    Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the inclusive byte range [start, end] and invokes
    /// <paramref name="onChunk"/> for every chunk with the absolute file
    /// offset the chunk belongs to. Throws
    /// <see cref="TransportException"/> on failure.
    /// </summary>
    Task DownloadRangeAsync(
        string url,
        long start,
        long end,
        Func<ReadOnlyMemory<byte>, long, Task> onChunk,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams the whole resource from the start (no Range). Used only
    /// when the server does not support Range requests.
    /// </summary>
    Task DownloadToEndAsync(
        string url,
        Func<ReadOnlyMemory<byte>, long, Task> onChunk,
        CancellationToken cancellationToken);
}

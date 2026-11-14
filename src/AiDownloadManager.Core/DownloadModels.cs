namespace AiDownloadManager.Core;

/// <summary>A closed, inclusive byte range [Start, End].</summary>
public sealed record ByteRange(long Start, long End)
{
    public long Length => End - Start + 1;

    public bool Overlaps(ByteRange other) => Start <= other.End && other.Start <= End;
}

public enum DownloadState
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// A download job. The job itself carries no mutable progress; completed
/// byte ranges live in <see cref="IDownloadJournal"/> so a download can
/// be resumed by a new process.
/// </summary>
public sealed record DownloadJob(
    Guid Id,
    string Url,
    string DestinationPath,
    long? TotalBytes,
    string? ExpectedSha256,
    DownloadState State,
    DateTimeOffset CreatedAt);

public sealed record DownloadProgress(
    long DownloadedBytes,
    long? TotalBytes,
    double BytesPerSecond,
    int ActiveConnections);

public sealed record DownloadOutcome(
    bool Success,
    bool Cancelled,
    bool UsedRangeFallback,
    long BytesDownloaded,
    TimeSpan Duration,
    string? Sha256,
    string? ErrorMessage);

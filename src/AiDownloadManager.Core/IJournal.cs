namespace AiDownloadManager.Core;

/// <summary>
/// Crash-safe persistence for download jobs and their completed byte
/// ranges. The engine treats the journal as the source of truth for
/// resume: whatever is recorded here is assumed already on disk.
/// </summary>
public interface IDownloadJournal
{
    Task SaveJobAsync(DownloadJob job, CancellationToken cancellationToken = default);
    Task<DownloadJob?> LoadJobAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>All jobs, oldest first. The journal is the source of
    /// truth for what survives an application restart.</summary>
    Task<IReadOnlyList<DownloadJob>> ListJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a job and its completed ranges. A no-op when
    /// the id is unknown.</summary>
    Task DeleteJobAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateJobStateAsync(Guid id, DownloadState state, string? errorMessage, CancellationToken cancellationToken = default);

    /// <summary>Replaces the completed-range set for a job (atomic).</summary>
    Task SaveCompletedRangesAsync(Guid id, IReadOnlyList<ByteRange> completed, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ByteRange>> LoadCompletedRangesAsync(Guid id, CancellationToken cancellationToken = default);
}

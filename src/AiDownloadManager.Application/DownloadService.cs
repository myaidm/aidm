using System.Collections.Concurrent;
using AiDownloadManager.Core;
using AiDownloadManager.Engine;

namespace AiDownloadManager.Application;

/// <summary>
/// Thin application service over the engine. The WPF presentation
/// layer talks to this, never to the engine directly.
///
/// Job lifecycle (Phase 1 closure): a download is started through
/// <see cref="StartDownloadAsync"/> (or resumed by id through
/// <see cref="ResumeDownloadAsync"/>), which returns a
/// <see cref="DownloadHandle"/> — the job id is available immediately
/// and the <see cref="DownloadHandle.Completion"/> task yields the
/// outcome. <see cref="PauseAsync"/> and <see cref="CancelAsync"/>
/// address a running download by id; the service tracks in-flight
/// work so a pause or cancel reaches the right download. The journal
/// stays the source of truth for resume: <see cref="ListJobsAsync"/>
/// returns every persisted job (so history survives a restart) and
/// <see cref="DeleteJobAsync"/> removes one.
///
/// Optional antivirus hook (Phase 1): when an
/// <see cref="AntivirusScanner"/> is supplied and enabled, every
/// completed download is scanned. The scan result is exposed as
/// <see cref="LastScanResult"/> and recorded on the job through the
/// journal's existing state/error fields — no new table:
/// Clean leaves the job Completed; Infected marks the job Failed
/// (the file is left in place for the user to deal with — it is
/// never deleted or executed by the hook); a scan Error or timeout
/// leaves the job Completed but records the scan problem in the
/// job's error field, because the download itself succeeded.
/// Scanning is off by default; see <see cref="AntivirusScannerOptions"/>.
/// </summary>
public sealed class DownloadService
{
    private readonly Func<SegmentedDownloaderOptions, SegmentedDownloader> _downloaderFactory;
    private readonly IDownloadJournal _journal;
    private readonly AntivirusScanner? _antivirusScanner;

    /// <summary>In-flight downloads, by job id. Removed when the tracked task finishes.</summary>
    private readonly ConcurrentDictionary<Guid, InFlight> _inFlight = new();

    private sealed record InFlight(CancellationTokenSource Cts, Task<DownloadOutcome> Task);

    public DownloadService(
        SegmentedDownloader downloader,
        IDownloadJournal journal,
        AntivirusScanner? antivirusScanner = null)
        : this(journal, _ => downloader, antivirusScanner)
    {
    }

    /// <summary>
    /// Builds one downloader per download (so each download can use
    /// its own connection count). The single-downloader constructor
    /// above is a convenience that wraps one instance as a factory
    /// which ignores the options.
    /// </summary>
    public DownloadService(
        IDownloadJournal journal,
        Func<SegmentedDownloaderOptions, SegmentedDownloader> downloaderFactory,
        AntivirusScanner? antivirusScanner = null)
    {
        _journal = journal;
        _downloaderFactory = downloaderFactory;
        _antivirusScanner = antivirusScanner;
    }

    /// <summary>The result of the most recent post-download scan, if one ran.</summary>
    public ScanResult? LastScanResult { get; private set; }

    public async Task<DownloadOutcome> DownloadAsync(
        string url,
        string destinationPath,
        string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var job = new DownloadJob(
            Guid.NewGuid(), url, destinationPath, null, expectedSha256,
            DownloadState.Queued, DateTimeOffset.UtcNow);
        await _journal.SaveJobAsync(job, cancellationToken);
        return await RunDownloadAsync(job, progress,
            new SegmentedDownloaderOptions(), cancellationToken);
    }

    public async Task<DownloadOutcome> ResumeAsync(
        Guid jobId,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var job = await _journal.LoadJobAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"No job with id {jobId}");
        return await RunDownloadAsync(job, progress,
            new SegmentedDownloaderOptions(), cancellationToken);
    }

    /// <summary>
    /// Starts a download as tracked background work and returns a
    /// handle immediately: the job id is usable right away (pause,
    /// cancel, resume) and <see cref="DownloadHandle.Completion"/>
    /// yields the outcome. The job is saved to the journal first, so
    /// it survives a crash even if the download never starts.
    /// </summary>
    public async Task<DownloadHandle> StartDownloadAsync(
        DownloadJob job,
        IProgress<DownloadProgress>? progress = null,
        int maxConnections = 8,
        CancellationToken cancellationToken = default)
    {
        await _journal.SaveJobAsync(job, cancellationToken);
        return StartTracked(job, progress, maxConnections, cancellationToken);
    }

    /// <summary>
    /// Resumes a persisted job by id as tracked background work and
    /// returns a handle. Throws <see cref="InvalidOperationException"/>
    /// when the id is unknown.
    /// </summary>
    public async Task<DownloadHandle> ResumeDownloadAsync(
        Guid jobId,
        IProgress<DownloadProgress>? progress = null,
        int maxConnections = 8,
        CancellationToken cancellationToken = default)
    {
        var job = await _journal.LoadJobAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"No job with id {jobId}");
        return StartTracked(job, progress, maxConnections, cancellationToken);
    }

    /// <summary>
    /// Pauses a running download: cancels its token. The partial file
    /// and the journal ranges are kept, so a later resume continues
    /// where it stopped. A no-op when the job is not running.
    /// </summary>
    public Task PauseAsync(Guid jobId)
    {
        if (_inFlight.TryGetValue(jobId, out var inFlight))
            inFlight.Cts.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels a download and discards its partial data: the running
    /// engine (if any) is stopped first and only then are the journal
    /// ranges cleared and the <c>.part</c> file deleted, so the
    /// engine's final persist cannot race the discard. A completed
    /// job is left alone (its file belongs to the user). The caller
    /// owns deleting a completed file; this method never does.
    /// </summary>
    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _journal.LoadJobAsync(jobId, cancellationToken);
        if (job is null || job.State == DownloadState.Completed)
            return;

        if (_inFlight.TryGetValue(jobId, out var inFlight))
        {
            inFlight.Cts.Cancel();
            try { await inFlight.Task; } catch { }
        }

        await _journal.SaveCompletedRangesAsync(jobId, Array.Empty<ByteRange>(), cancellationToken);
        try
        {
            var partPath = job.DestinationPath + ".part";
            if (File.Exists(partPath))
                File.Delete(partPath);
        }
        catch
        {
            // Best effort: the .part file is inert without journal ranges.
        }
        await _journal.UpdateJobStateAsync(jobId, DownloadState.Cancelled, null, cancellationToken);
    }

    /// <summary>Every persisted job, oldest first.</summary>
    public Task<IReadOnlyList<DownloadJob>> ListJobsAsync(CancellationToken cancellationToken = default)
        => _journal.ListJobsAsync(cancellationToken);

    /// <summary>
    /// Deletes a job and its completed ranges from the journal. Any
    /// in-flight download for the job is cancelled first. Files are
    /// never deleted by this method — the caller decides about them.
    /// A no-op when the id is unknown.
    /// </summary>
    public async Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_inFlight.TryGetValue(jobId, out var inFlight))
        {
            inFlight.Cts.Cancel();
            try { await inFlight.Task; } catch { }
        }
        await _journal.DeleteJobAsync(jobId, cancellationToken);
    }

    private DownloadHandle StartTracked(
        DownloadJob job,
        IProgress<DownloadProgress>? progress,
        int maxConnections,
        CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The task's finally removes its own entry; it reads the
        // record through this closure because the record is built
        // from the task itself. The assignment runs synchronously
        // right after the task is created, before any await in the
        // task can complete (the download always does async I/O).
        InFlight? inFlight = null;
        var task = RunTrackedAsync(job, progress, maxConnections, cts, () => inFlight);
        inFlight = new InFlight(cts, task);
        _inFlight[job.Id] = inFlight;
        return new DownloadHandle(job.Id, job, task);
    }

    private async Task<DownloadOutcome> RunTrackedAsync(
        DownloadJob job,
        IProgress<DownloadProgress>? progress,
        int maxConnections,
        CancellationTokenSource cts,
        Func<InFlight?> getInFlight)
    {
        try
        {
            return await RunDownloadAsync(job, progress,
                new SegmentedDownloaderOptions(MaxConnections: maxConnections), cts.Token);
        }
        finally
        {
            // Remove only if this run is still the tracked one (a
            // resume may have replaced the entry for the same id).
            var self = getInFlight();
            if (self is not null)
            {
                ((ICollection<KeyValuePair<Guid, InFlight>>)_inFlight)
                    .Remove(new KeyValuePair<Guid, InFlight>(job.Id, self));
            }
            cts.Dispose();
        }
    }

    private async Task<DownloadOutcome> RunDownloadAsync(
        DownloadJob job,
        IProgress<DownloadProgress>? progress,
        SegmentedDownloaderOptions options,
        CancellationToken cancellationToken)
    {
        var downloader = _downloaderFactory(options);
        var outcome = await downloader.DownloadAsync(job, progress, cancellationToken);
        return await RunPostCompletionScanAsync(job, outcome, cancellationToken);
    }

    /// <summary>
    /// Scans a completed download only: skipped when the download
    /// failed or was cancelled, and skipped entirely when no scanner
    /// is configured or scanning is disabled.
    /// </summary>
    private async Task<DownloadOutcome> RunPostCompletionScanAsync(
        DownloadJob job, DownloadOutcome outcome, CancellationToken cancellationToken)
    {
        if (!outcome.Success || outcome.Cancelled || _antivirusScanner is not { Enabled: true })
            return outcome;

        var result = await _antivirusScanner.ScanAsync(job.DestinationPath, cancellationToken);
        LastScanResult = result;
        switch (result.Verdict)
        {
            case ScanVerdict.Clean:
                return outcome;
            case ScanVerdict.Infected:
                await _journal.UpdateJobStateAsync(job.Id, DownloadState.Failed,
                    $"Antivirus scan reported an infection: {result.Output}",
                    CancellationToken.None);
                return outcome with
                {
                    Success = false,
                    ErrorMessage = "Antivirus scan reported an infection in the downloaded file."
                };
            default:
                // Error / TimedOut: the download itself succeeded, so the
                // job stays Completed; the scan problem is recorded in
                // the job's error field for the user to see.
                await _journal.UpdateJobStateAsync(job.Id, DownloadState.Completed,
                    $"Antivirus scan {result.Verdict}: {result.ErrorMessage}",
                    CancellationToken.None);
                return outcome;
        }
    }
}

/// <summary>
/// A started download: its job id (available immediately) and the
/// task that completes with the outcome. Awaiting
/// <see cref="Completion"/> is the only way to learn the result;
/// the service does not raise completion events.
/// </summary>
public sealed record DownloadHandle(Guid JobId, DownloadJob Job, Task<DownloadOutcome> Completion);

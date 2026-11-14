using System.Diagnostics;
using System.Security.Cryptography;
using AiDownloadManager.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiDownloadManager.Engine;

public sealed record SegmentedDownloaderOptions(
    int MaxConnections = 8,
    long MinSegmentBytes = 1024 * 1024,
    int MaxAttemptsPerRange = 5,
    TimeSpan? BaseRetryDelay = null,
    /// <summary>
    /// Bytes of newly completed data that trigger a journal flush.
    /// Larger values mean fewer (faster) SQLite transactions; a crash
    /// loses at most this many bytes of completed-but-unflushed progress
    /// (plus one in-flight chunk per worker). See ARCHITECTURE.md.
    /// </summary>
    long JournalCheckpointBytes = 4 * 1024 * 1024,
    /// <summary>
    /// Maximum time between journal flushes even when little data has
    /// accumulated (backstop for slow downloads). A flush also always
    /// happens on cancellation, failure, and completion.
    /// </summary>
    TimeSpan? JournalCheckpointInterval = null)
{
    public TimeSpan BaseRetryDelayValue => BaseRetryDelay ?? TimeSpan.FromSeconds(1);
    public TimeSpan JournalCheckpointIntervalValue => JournalCheckpointInterval ?? TimeSpan.FromSeconds(2);
}

/// <summary>
/// Segmented download engine. UI-independent: it only knows about
/// <see cref="ITransport"/> and <see cref="IDownloadJournal"/>.
///
/// Guarantees:
/// - Byte ranges never overlap (the work queue hands out disjoint ranges).
/// - A file is only promoted to its destination after its length and,
///   when supplied, its SHA-256 have been verified.
/// - A cancelled or crashed download can be resumed from the journal;
///   at most <see cref="SegmentedDownloaderOptions.JournalCheckpointBytes"/>
///   of completed-but-unflushed progress per download is re-downloaded
///   (plus one in-flight chunk per worker).
/// - A server that does not support Range requests is detected by the
///   probe and downloaded with a single connection instead of failing.
///   Single-connection downloads retry transient failures and resume
///   from the last safely committed prefix by skipping already-downloaded
///   bytes from a fresh stream; the final length/hash check still applies,
///   so a server that returns different content on retry fails safely
///   instead of promoting a corrupt file.
/// </summary>
public sealed class SegmentedDownloader
{
    private readonly ITransport _transport;
    private readonly IDownloadJournal _journal;
    private readonly ILogger<SegmentedDownloader> _logger;
    private readonly SegmentedDownloaderOptions _options;

    public SegmentedDownloader(
        ITransport transport,
        IDownloadJournal journal,
        ILogger<SegmentedDownloader>? logger = null,
        SegmentedDownloaderOptions? options = null)
    {
        _transport = transport;
        _journal = journal;
        _logger = logger ?? NullLogger<SegmentedDownloader>.Instance;
        _options = options ?? new SegmentedDownloaderOptions();
    }

    public async Task<DownloadOutcome> DownloadAsync(
        DownloadJob job,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var partPath = job.DestinationPath + ".part";
        var completed = new List<ByteRange>();
        long downloadedBaseline = 0;

        try
        {
            await _journal.SaveJobAsync(job with { State = DownloadState.Downloading }, cancellationToken);
            completed = (await _journal.LoadCompletedRangesAsync(job.Id, cancellationToken)).ToList();
            downloadedBaseline = completed.Sum(r => r.Length);

            var probe = await ProbeWithRetryAsync(job.Url, cancellationToken);
            long? totalBytes = probe.TotalBytes ?? job.TotalBytes;
            if (job.TotalBytes is not null && probe.TotalBytes is not null
                && job.TotalBytes.Value != probe.TotalBytes.Value)
            {
                throw new InvalidOperationException(
                    $"Server file size changed: job expects {job.TotalBytes} bytes, server reports {probe.TotalBytes}");
            }

            if (!probe.SupportsRange || totalBytes is null)
            {
                _logger.LogInformation("Server does not support Range requests; using single-connection fallback.");
                // A previous run may have checkpointed a completed prefix
                // [0, N-1]. That prefix CAN be resumed without Range support:
                // the retry loop re-requests the whole file and skips the
                // first N bytes of the fresh stream. Anything that is not a
                // prefix starting at 0 cannot be trusted without Range
                // support and is discarded.
                var resumeFrom = PrefixLength(completed);
                var partLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                if (partLength < resumeFrom)
                {
                    _logger.LogWarning("Part file is {PartLength} bytes but the journal claims a {ResumeFrom}-byte prefix; resuming from the part file length.",
                        partLength, resumeFrom);
                    resumeFrom = partLength;
                }
                if (resumeFrom == 0 && completed.Count > 0)
                {
                    _logger.LogWarning("Discarding {Count} previously completed ranges: they are not a resumable prefix without Range support.", completed.Count);
                    completed.Clear();
                    await _journal.SaveCompletedRangesAsync(job.Id, completed, cancellationToken);
                }
                else if (resumeFrom > 0)
                {
                    completed.Clear();
                    completed.Add(new ByteRange(0, resumeFrom - 1));
                }

                await DownloadSingleConnectionAsync(job, partPath, resumeFrom, completed, progress, cancellationToken);
                return await FinishAsync(job, partPath, totalBytes, usedRangeFallback: true, stopwatch, cancellationToken);
            }

            var total = totalBytes.Value;
            var whole = new ByteRange(0, total - 1);
            var remaining = SegmentPlan.SubtractRanges(whole, completed);
            if (remaining.Count == 0)
            {
                _logger.LogInformation("All ranges already complete; verifying.");
                return await FinishAsync(job, partPath, total, usedRangeFallback: false, stopwatch, cancellationToken);
            }

            await DownloadSegmentedAsync(job, partPath, total, remaining, completed, downloadedBaseline, progress, cancellationToken);
            return await FinishAsync(job, partPath, total, usedRangeFallback: false, stopwatch, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Persist with CancellationToken.None: the caller's token is
            // already cancelled, and passing it would make the journal
            // write throw immediately, losing the in-memory progress.
            // (This bug was masked in Phase 0 by the frequent journal
            // flushes; the coalesced flushing in Phase 1 exposed it.)
            await PersistAsync(job.Id, completed, CancellationToken.None);
            await _journal.UpdateJobStateAsync(job.Id, DownloadState.Cancelled, "Cancelled by user", CancellationToken.None);
            var confirmed = SegmentPlan.MergeRanges(completed).Sum(r => r.Length);
            return new DownloadOutcome(false, true, false, confirmed, stopwatch.Elapsed, null, "Cancelled by user");
        }
        catch (Exception ex)
        {
            await PersistAsync(job.Id, completed, CancellationToken.None);
            await _journal.UpdateJobStateAsync(job.Id, DownloadState.Failed, ex.Message, CancellationToken.None);
            _logger.LogError(ex, "Download {JobId} failed", job.Id);
            var confirmed = SegmentPlan.MergeRanges(completed).Sum(r => r.Length);
            return new DownloadOutcome(false, false, false, confirmed, stopwatch.Elapsed, null, ex.Message);
        }
    }

    private async Task<ProbeResult> ProbeWithRetryAsync(string url, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await _transport.ProbeAsync(url, ct);
            }
            catch (TransportException ex) when (ex.Kind == TransportFailureKind.Transient)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
            }
        }
        throw lastError!;
    }

    /// <summary>
    /// Single-connection download for servers without Range support, with
    /// the same retry discipline as the segmented path. Each attempt
    /// re-requests the whole file and skips the bytes already safely on
    /// disk, so a dropped connection resumes from the last committed
    /// prefix instead of restarting. The final length/hash check in
    /// <see cref="FinishAsync"/> still applies, so a server that returns
    /// different content on a retry attempt fails safely rather than
    /// promoting a corrupt file. Progress is checkpointed to the journal
    /// as the prefix [0, written-1] so an application restart can resume.
    /// </summary>
    private async Task DownloadSingleConnectionAsync(
        DownloadJob job, string partPath, long resumeFrom,
        List<ByteRange> completed,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (file.Length < resumeFrom)
            throw new InvalidOperationException(
                $"Part file is {file.Length} bytes but resume was requested from {resumeFrom}");
        file.Seek(resumeFrom, SeekOrigin.Begin);
        long written = resumeFrom;
        var started = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long streamBytesSeen = 0;
            try
            {
                await _transport.DownloadToEndAsync(job.Url, async (chunk, offset) =>
                {
                    streamBytesSeen += chunk.Length;
                    // Skip bytes already on disk: the stream always starts
                    // at 0 for a server without Range support.
                    var skip = Math.Min(written, streamBytesSeen) - (streamBytesSeen - chunk.Length);
                    if (skip < 0) skip = 0;
                    if (skip >= chunk.Length)
                        return;
                    var payload = chunk.Slice((int)skip);
                    file.Seek(written, SeekOrigin.Begin);
                    await file.WriteAsync(payload, ct);
                    written += payload.Length;

                    progress?.Report(new DownloadProgress(written, job.TotalBytes,
                        written / Math.Max(started.Elapsed.TotalSeconds, 0.001), 1));

                    // In-memory progress is always current (cheap: one
                    // range), so a graceful cancel resumes from the last
                    // chunk. Only the journal flush is throttled: one
                    // synchronous SQLite transaction per
                    // JournalCheckpointBytes. A hard crash therefore loses
                    // at most JournalCheckpointBytes; a graceful cancel
                    // loses at most one 64 KB chunk.
                    completed.Clear();
                    completed.Add(new ByteRange(0, written - 1));
                    if (written - resumeFrom >= _options.JournalCheckpointBytes)
                    {
                        await _journal.SaveCompletedRangesAsync(job.Id, completed, ct);
                        resumeFrom = written;
                    }
                }, ct);

                await file.FlushAsync(ct);

                if (streamBytesSeen < written)
                {
                    // The server returned fewer bytes on this attempt than
                    // we already have on disk (content changed or truncated).
                    // Retrying cannot fix that; fail permanently instead of
                    // looping or promoting a short file.
                    throw new InvalidOperationException(
                        $"Server returned fewer bytes ({streamBytesSeen}) than already downloaded ({written}); the server content changed or was truncated.");
                }
                return;
            }
            catch (TransportException ex) when (ex.Kind == TransportFailureKind.Transient && attempt < _options.MaxAttemptsPerRange)
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(
                    _options.BaseRetryDelayValue.TotalMilliseconds * (1 << (attempt - 1)));
                _logger.LogWarning(ex, "Transient failure on single-connection download, attempt {Attempt}; resuming from byte {Written} in {Delay}.",
                    attempt, written, delay);
                await Task.Delay(delay, ct);
                // written is unchanged: the next attempt skips exactly the
                // bytes already on disk and appends after them.
            }
        }
    }

    /// <summary>
    /// Returns the length of the completed prefix [0, N-1] when the
    /// completed ranges start at 0, else 0. Only a prefix is resumable
    /// without Range support.
    /// </summary>
    private static long PrefixLength(IReadOnlyList<ByteRange> completed)
    {
        var merged = SegmentPlan.MergeRanges(completed);
        if (merged.Count == 0 || merged[0].Start != 0)
            return 0;
        return merged[0].End + 1;
    }

    private async Task DownloadSegmentedAsync(
        DownloadJob job, string partPath, long total,
        IReadOnlyList<ByteRange> remaining, List<ByteRange> completed,
        long downloadedBaseline, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        // Pre-allocate so workers can seek to any offset immediately.
        using (var prealloc = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            if (prealloc.Length != total)
                prealloc.SetLength(total);
        }

        var queue = new WorkQueue(remaining, _options.MinSegmentBytes);
        var downloaded = downloadedBaseline;
        var started = Stopwatch.StartNew();
        var journalLock = new SemaphoreSlim(1, 1);
        var reportLock = new object();

        // Coalesced journal flushing: a flush writes one transaction, so
        // flushing on every 256 KB chunk (Phase 0 behavior) spent most of
        // the download time in SQLite fsyncs. Now a flush happens only
        // when enough new data accumulated or enough time passed — and
        // always on completion, cancellation, or failure. A crash loses
        // at most JournalCheckpointBytes of completed-but-unflushed
        // progress (plus one in-flight chunk per worker); nothing is
        // ever reported as saved before its transaction commits.
        var lastCheckpointBytes = downloadedBaseline;
        var lastCheckpointAt = Stopwatch.GetTimestamp();
        var checkpointIntervalTicks = (long)(_options.JournalCheckpointIntervalValue.TotalSeconds * Stopwatch.Frequency);

        // A linked token so that one worker failing stops the others
        // promptly instead of leaving them writing after the download
        // has already been marked failed.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workerCt = linkedCts.Token;

        async Task PersistCompletedAsync(bool force = false)
        {
            // Cheap pre-check without taking the lock: in the common case
            // nothing is due and this returns without any I/O.
            var newBytes = Volatile.Read(ref downloaded) - lastCheckpointBytes;
            var now = Stopwatch.GetTimestamp();
            if (!force && newBytes < _options.JournalCheckpointBytes
                && now - lastCheckpointAt < checkpointIntervalTicks)
                return;

            List<ByteRange> snapshot;
            lock (completed)
            {
                snapshot = SegmentPlan.MergeRanges(completed).ToList();
            }
            await journalLock.WaitAsync(ct);
            try
            {
                // Re-check under the lock: another worker may have
                // flushed while we were waiting.
                var newBytesAfterWait = Volatile.Read(ref downloaded) - lastCheckpointBytes;
                var nowAfterWait = Stopwatch.GetTimestamp();
                if (!force && newBytesAfterWait < _options.JournalCheckpointBytes
                    && nowAfterWait - lastCheckpointAt < checkpointIntervalTicks)
                    return;
                await _journal.SaveCompletedRangesAsync(job.Id, snapshot, ct);
                lastCheckpointBytes = Volatile.Read(ref downloaded);
                lastCheckpointAt = nowAfterWait;
            }
            finally
            {
                journalLock.Release();
            }
        }

        async Task WorkerAsync()
        {
            using var file = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            while (true)
            {
                workerCt.ThrowIfCancellationRequested();
                var range = queue.Claim();
                if (range is null)
                    return;
                await DownloadRangeWithRetryAsync(job.Url, range, file, async (newlyCompleted) =>
                {
                    lock (completed)
                    {
                        completed.Add(newlyCompleted);
                    }
                    Interlocked.Add(ref downloaded, newlyCompleted.Length);
                    lock (reportLock)
                    {
                        progress?.Report(new DownloadProgress(
                            Volatile.Read(ref downloaded), total,
                            Volatile.Read(ref downloaded) / Math.Max(started.Elapsed.TotalSeconds, 0.001),
                            _options.MaxConnections));
                    }
                    // Coalesced: usually a no-op unless a flush is due.
                    await PersistCompletedAsync();
                }, workerCt);
                await PersistCompletedAsync();
            }
        }

        var workers = Enumerable.Range(0, _options.MaxConnections)
            .Select(_ => WorkerAsync())
            .ToArray();
        try
        {
            await Task.WhenAll(workers);
            // Final flush: record everything before verification, so a
            // crash between here and promotion still resumes correctly.
            await PersistCompletedAsync(force: true);
        }
        catch
        {
            // Stop the remaining workers and wait for them before the
            // caller observes the failure; otherwise they could keep
            // writing to the part file and the journal afterwards.
            linkedCts.Cancel();
            try { await Task.WhenAll(workers); } catch { /* already recorded */ }
            throw;
        }
    }

    /// <summary>
    /// In-memory progress granularity: completed bytes are recorded in the
    /// in-memory range list every 256 KB. This is deliberately separate
    /// from <see cref="SegmentedDownloaderOptions.JournalCheckpointBytes"/>
    /// (the journal flush threshold): recording in memory is cheap, while
    /// a journal flush is a synchronous SQLite transaction.
    /// </summary>
    private const long MemoryCheckpointBytes = 256 * 1024;

    /// <summary>Downloads one range with per-chunk writes and retries on transient failures.</summary>
    private async Task DownloadRangeWithRetryAsync(
        string url, ByteRange range, FileStream file,
        Func<ByteRange, Task> onRangePartCompleted,
        CancellationToken ct)
    {
        var cursor = range.Start;
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long rangeDownloaded = 0;
            try
            {
                await _transport.DownloadRangeAsync(url, cursor, range.End, async (chunk, offset) =>
                {
                    file.Seek(offset, SeekOrigin.Begin);
                    await file.WriteAsync(chunk, ct);
                    rangeDownloaded += chunk.Length;
                    if (rangeDownloaded >= MemoryCheckpointBytes)
                    {
                        await onRangePartCompleted(new ByteRange(cursor, offset + chunk.Length - 1));
                        cursor = offset + chunk.Length;
                        rangeDownloaded = 0;
                    }
                }, ct);

                if (rangeDownloaded > 0)
                    await onRangePartCompleted(new ByteRange(cursor, range.End));
                return;
            }
            catch (TransportException ex) when (ex.Kind == TransportFailureKind.Transient && attempt < _options.MaxAttemptsPerRange)
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(
                    _options.BaseRetryDelayValue.TotalMilliseconds * (1 << (attempt - 1)));
                _logger.LogWarning(ex, "Transient failure on range {Start}-{End}, attempt {Attempt}; retrying in {Delay}.",
                    cursor, range.End, attempt, delay);
                await Task.Delay(delay, ct);
                // cursor already points at the first byte not yet
                // checkpointed, so the next attempt resumes exactly there.
            }
        }
    }

    private async Task PersistAsync(Guid jobId, List<ByteRange> completed, CancellationToken ct)
    {
        try
        {
            var merged = SegmentPlan.MergeRanges(completed).ToList();
            await _journal.SaveCompletedRangesAsync(jobId, merged, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist journal for {JobId}", jobId);
        }
    }

    private async Task<DownloadOutcome> FinishAsync(
        DownloadJob job, string partPath, long? totalBytes,
        bool usedRangeFallback, Stopwatch stopwatch, CancellationToken ct)
    {
        var fileInfo = new FileInfo(partPath);
        if (!fileInfo.Exists)
            throw new InvalidOperationException("Download finished but the partial file is missing");
        if (totalBytes is not null && fileInfo.Length != totalBytes.Value)
            throw new InvalidOperationException(
                $"Downloaded file length {fileInfo.Length} does not match expected {totalBytes}");

        var sha256 = await ComputeSha256Async(partPath, ct);
        if (job.ExpectedSha256 is not null &&
            !string.Equals(sha256, job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SHA-256 mismatch: expected {job.ExpectedSha256}, got {sha256}. The partial file was kept for diagnosis.");
        }

        File.Move(partPath, job.DestinationPath, overwrite: true);
        await _journal.UpdateJobStateAsync(job.Id, DownloadState.Completed, null, ct);
        var downloaded = totalBytes ?? fileInfo.Length;
        return new DownloadOutcome(true, false, usedRangeFallback, downloaded, stopwatch.Elapsed, sha256, null);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Thread-safe work queue. Claims hand out disjoint ranges; claiming
    /// splits the largest remaining range so idle workers always have
    /// useful work (dynamic segmentation).
    /// </summary>
    private sealed class WorkQueue
    {
        private readonly List<ByteRange> _remaining;
        private readonly long _minSegmentBytes;
        private readonly object _gate = new();

        public WorkQueue(IReadOnlyList<ByteRange> remaining, long minSegmentBytes)
        {
            _remaining = remaining.ToList();
            _minSegmentBytes = minSegmentBytes;
        }

        public ByteRange? Claim()
        {
            lock (_gate)
            {
                if (_remaining.Count == 0)
                    return null;
                var largest = _remaining.OrderByDescending(r => r.Length).First();
                _remaining.Remove(largest);
                if (largest.Length >= 2 * _minSegmentBytes)
                {
                    var split = SegmentPlan.SplitLargest(new[] { largest }, _minSegmentBytes)!.Value;
                    _remaining.Add(split.First);
                    return split.Second;
                }
                return largest;
            }
        }
    }
}

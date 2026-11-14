using AiDownloadManager.Core;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// SQLite checkpoint optimization and crash-safety tests (Phase 1).
/// The engine now coalesces journal flushes (one transaction per
/// JournalCheckpointBytes, plus a time-based backstop, plus a forced
/// flush on completion/cancel/failure) instead of flushing on every
/// 256 KB chunk. These tests prove the write count dropped AND that
/// recovery still works when the journal is interrupted.
/// </summary>
public class JournalCheckpointTests : IAsyncLifetime
{
    private string _workDir = string.Empty;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aidm-journal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private async Task<(DeterministicTestServer Server, string Sha256)> StartServerAsync(
        long sizeBytes, TestServerOptions options)
    {
        var source = Path.Combine(_workDir, $"source-{Guid.NewGuid():N}.bin");
        var sha = TestDataGenerator.Generate(source, sizeBytes, seed: 7);
        options = options with
        {
            UnixSocketPath = Path.Combine(_workDir, $"srv-{Guid.NewGuid():N}.sock")
        };
        var server = new DeterministicTestServer(source, options);
        await server.StartAsync();
        return (server, sha);
    }

    [Fact]
    public async Task JournalFlushes_AreCoalesced_FarFewerWritesThanPhase0()
    {
        // 20 MB with a 4 MB checkpoint threshold and the time-based flush
        // disabled: expect ~5 byte-triggered flushes + 1 final flush.
        // Phase 0 flushed on every 256 KB chunk (~80 writes for 20 MB).
        const long size = 20 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size, new TestServerOptions(size));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var realJournal = new SqliteDownloadJournal(Path.Combine(_workDir, "counted.db"));
        await realJournal.InitializeAsync();
        var counting = new CountingJournal(realJournal);
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        var options = new SegmentedDownloaderOptions(
            MaxConnections: 4,
            JournalCheckpointBytes: 4 * 1024 * 1024,
            JournalCheckpointInterval: TimeSpan.FromMinutes(60));

        var outcome = await new SegmentedDownloader(new HttpTransport(client), counting, options: options)
            .DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
        // 5 byte-triggered + 1 final forced flush = 6; allow one extra
        // for timing/rounding. Phase 0 would have done ~80.
        Assert.InRange(counting.SaveCompletedRangesCalls, 1, 7);
    }

    [Fact]
    public async Task InterruptedJournalWrite_FailsSafely_AndResumeWithWorkingJournalSucceeds()
    {
        // The journal throws on every completed-range write, modelling a
        // disk failure or a kill mid-transaction. The download must fail
        // (it cannot record progress safely) without promoting a file,
        // and a later resume with a working journal over the same
        // database must re-download the missing ranges and succeed —
        // proving the partial state left behind is consistent.
        const long size = 10 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size, new TestServerOptions(size));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var dbPath = Path.Combine(_workDir, "shared.db");
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var realJournal = new SqliteDownloadJournal(dbPath);
        await realJournal.InitializeAsync();
        var throwing = new ThrowingJournal(realJournal);
        var first = await new SegmentedDownloader(new HttpTransport(client), throwing)
            .DownloadAsync(job);
        Assert.False(first.Success, "A download that cannot journal its progress must fail, not pretend to succeed");
        Assert.NotNull(first.ErrorMessage);
        Assert.False(File.Exists(destination), "A failed download must never be promoted");

        // The failed run persisted nothing (every range write threw), so
        // the resume starts from the journal's empty range set and must
        // still reach the correct hash.
        var workingJournal = new SqliteDownloadJournal(dbPath);
        await workingJournal.InitializeAsync();
        var resumed = await workingJournal.LoadCompletedRangesAsync(job.Id);
        Assert.Empty(resumed);
        var second = await new SegmentedDownloader(new HttpTransport(client), workingJournal)
            .DownloadAsync(job with { State = DownloadState.Queued });
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(sha, second.Sha256);
    }

    [Fact]
    public async Task CrashLikeInterruption_LeavesNoCorruptFile_AndResumeVerifiesHash()
    {
        // Cancel mid-download (the closest in-process model of a
        // process kill: the part file and journal are left as-is), then
        // resume with a fresh engine and journal instance. The resumed
        // download must verify the full SHA-256 before promoting.
        const long size = 20 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, ThrottleBytesPerSecond: 2 * 1024 * 1024));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var dbPath = Path.Combine(_workDir, "shared.db");
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        using (var cts = new CancellationTokenSource())
        {
            var journal1 = new SqliteDownloadJournal(dbPath);
            await journal1.InitializeAsync();
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.TotalBytes is not null && p.DownloadedBytes * 100 / p.TotalBytes.Value >= 40)
                    cts.Cancel();
            });
            var first = await new SegmentedDownloader(new HttpTransport(client), journal1)
                .DownloadAsync(job, progress, cts.Token);
            Assert.True(first.Cancelled);
        }
        Assert.False(File.Exists(destination));

        var journal2 = new SqliteDownloadJournal(dbPath);
        await journal2.InitializeAsync();
        var resumedRanges = await journal2.LoadCompletedRangesAsync(job.Id);
        Assert.NotEmpty(resumedRanges);
        // Every recorded range must lie inside the file.
        foreach (var range in resumedRanges)
            Assert.True(range.Start >= 0 && range.End < size);

        var second = await new SegmentedDownloader(new HttpTransport(client), journal2)
            .DownloadAsync(job with { State = DownloadState.Queued });
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(sha, second.Sha256);
        Assert.Equal(size, new FileInfo(destination).Length);
    }

    /// <summary>Counts completed-range writes, delegating everything else.</summary>
    private sealed class CountingJournal : IDownloadJournal
    {
        private readonly IDownloadJournal _inner;
        public int SaveCompletedRangesCalls;

        public CountingJournal(IDownloadJournal inner) => _inner = inner;

        public Task SaveJobAsync(DownloadJob job, CancellationToken ct = default)
            => _inner.SaveJobAsync(job, ct);
        public Task<DownloadJob?> LoadJobAsync(Guid id, CancellationToken ct = default)
            => _inner.LoadJobAsync(id, ct);
        public Task<IReadOnlyList<DownloadJob>> ListJobsAsync(CancellationToken ct = default)
            => _inner.ListJobsAsync(ct);
        public Task DeleteJobAsync(Guid id, CancellationToken ct = default)
            => _inner.DeleteJobAsync(id, ct);
        public Task UpdateJobStateAsync(Guid id, DownloadState state, string? error, CancellationToken ct = default)
            => _inner.UpdateJobStateAsync(id, state, error, ct);
        public Task<IReadOnlyList<ByteRange>> LoadCompletedRangesAsync(Guid id, CancellationToken ct = default)
            => _inner.LoadCompletedRangesAsync(id, ct);

        public async Task SaveCompletedRangesAsync(Guid id, IReadOnlyList<ByteRange> completed, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SaveCompletedRangesCalls);
            await _inner.SaveCompletedRangesAsync(id, completed, ct);
        }
    }

    /// <summary>Fails every completed-range write, modelling a disk failure mid-download.</summary>
    private sealed class ThrowingJournal : IDownloadJournal
    {
        private readonly IDownloadJournal _inner;
        public ThrowingJournal(IDownloadJournal inner) => _inner = inner;

        public Task SaveJobAsync(DownloadJob job, CancellationToken ct = default)
            => _inner.SaveJobAsync(job, ct);
        public Task<DownloadJob?> LoadJobAsync(Guid id, CancellationToken ct = default)
            => _inner.LoadJobAsync(id, ct);
        public Task<IReadOnlyList<DownloadJob>> ListJobsAsync(CancellationToken ct = default)
            => _inner.ListJobsAsync(ct);
        public Task DeleteJobAsync(Guid id, CancellationToken ct = default)
            => _inner.DeleteJobAsync(id, ct);
        public Task UpdateJobStateAsync(Guid id, DownloadState state, string? error, CancellationToken ct = default)
            => _inner.UpdateJobStateAsync(id, state, error, ct);
        public Task<IReadOnlyList<ByteRange>> LoadCompletedRangesAsync(Guid id, CancellationToken ct = default)
            => _inner.LoadCompletedRangesAsync(id, ct);

        public Task SaveCompletedRangesAsync(Guid id, IReadOnlyList<ByteRange> completed, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated journal failure (disk error)");
    }
}

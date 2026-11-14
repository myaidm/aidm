using AiDownloadManager.Core;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// Adaptive connection allocation tests (Phase 1). The engine's dynamic
/// segmentation (a shared work queue that always splits the largest
/// remaining range) is verified, not assumed: different file sizes and
/// connection counts, uneven worker speed, single-worker failure, and
/// the connection limit. Measurement (see PHASE_1_PROGRESS.md) showed
/// that more connections are NOT always faster on a local server — 4
/// connections beat 8 and 16 on the 100 MB benchmark — so no engine
/// change was made here; these tests lock in the existing behavior.
/// </summary>
public class AdaptiveAllocationTests : IAsyncLifetime
{
    private string _workDir = string.Empty;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aidm-adaptive-" + Guid.NewGuid().ToString("N")[..8]);
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

    private IDownloadJournal CreateJournal()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, $"journal-{Guid.NewGuid():N}.db"));
        journal.InitializeAsync().GetAwaiter().GetResult();
        return journal;
    }

    [Fact]
    public async Task DifferentSizesAndConnectionCounts_AllSucceed_WithMatchingHash()
    {
        // 1 MB (smaller than the 1 MB minimum segment: a single range),
        // 5 MB, and 20 MB, each with 1, 4, 8, and 16 connections.
        foreach (var size in new long[] { 1 * 1024 * 1024, 5 * 1024 * 1024, 20 * 1024 * 1024 })
        {
            foreach (var connections in new[] { 1, 4, 8, 16 })
            {
                var (server, sha) = await StartServerAsync(size, new TestServerOptions(size));
                await using var _ = server;
                using var client = server.CreateHttpClient();
                var journal = CreateJournal();
                var destination = Path.Combine(_workDir, $"out-{size}-{connections}.bin");
                var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
                var options = new SegmentedDownloaderOptions(MaxConnections: connections);

                var outcome = await new SegmentedDownloader(new HttpTransport(client), journal, options: options)
                    .DownloadAsync(job);

                Assert.True(outcome.Success, $"size={size} connections={connections}: {outcome.ErrorMessage}");
                Assert.Equal(sha, outcome.Sha256);
            }
        }
    }

    [Fact]
    public async Task UnevenWorkerSpeed_SlowWorkerDoesNotBreakOrStallDownload()
    {
        // One range is served 2 s late (a slow connection). The other
        // workers must proceed concurrently; the download completes with
        // the correct hash once the slow range finishes.
        const long size = 20 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size, new TestServerOptions(size));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        var delaying = new DelayFirstRangeTransport(new HttpTransport(client), TimeSpan.FromSeconds(2));

        var outcome = await new SegmentedDownloader(delaying, journal,
            options: new SegmentedDownloaderOptions(MaxConnections: 4)).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
        Assert.Equal(1, delaying.DelayedCalls);
    }

    [Fact]
    public async Task SingleWorkerFailure_IsRetried_AndDownloadCompletes()
    {
        // The first range request fails once with a transient error.
        // Per-range retry must absorb it; the download must not fail.
        const long size = 10 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size, new TestServerOptions(size));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        var flaky = new FailFirstRangeTransport(new HttpTransport(client));

        var outcome = await new SegmentedDownloader(flaky, journal,
            options: new SegmentedDownloaderOptions(MaxConnections: 4)).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
        Assert.Equal(1, flaky.FailedCalls);
    }

    [Fact]
    public async Task ConnectionLimit_IsRespected_AndMultipleWorkersAreUsed()
    {
        const long size = 20 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size, new TestServerOptions(size));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        var counting = new ConcurrencyCountingTransport(new HttpTransport(client));

        var outcome = await new SegmentedDownloader(counting, journal,
            options: new SegmentedDownloaderOptions(MaxConnections: 8)).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
        Assert.True(counting.MaxConcurrent <= 8,
            $"Engine used {counting.MaxConcurrent} concurrent range requests with a limit of 8");
        Assert.True(counting.MaxConcurrent >= 2,
            $"Expected several workers to run concurrently, but max was {counting.MaxConcurrent}");
        Assert.True(counting.TotalCalls > 1, "Expected more than one range request");
    }

    /// <summary>Delays the first range request by a fixed amount, then behaves normally.</summary>
    private sealed class DelayFirstRangeTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly TimeSpan _delay;
        private int _calls;
        public int DelayedCalls;

        public DelayFirstRangeTransport(ITransport inner, TimeSpan delay)
        {
            _inner = inner;
            _delay = delay;
        }

        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct) => _inner.ProbeAsync(url, ct);
        public Task DownloadToEndAsync(string url, Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
            => _inner.DownloadToEndAsync(url, onChunk, ct);

        public async Task DownloadRangeAsync(string url, long start, long end,
            Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Interlocked.Increment(ref DelayedCalls);
                await Task.Delay(_delay, ct);
            }
            await _inner.DownloadRangeAsync(url, start, end, onChunk, ct);
        }
    }

    /// <summary>Fails the first range request once with a transient error, then behaves normally.</summary>
    private sealed class FailFirstRangeTransport : ITransport
    {
        private readonly ITransport _inner;
        private int _calls;
        public int FailedCalls;

        public FailFirstRangeTransport(ITransport inner) => _inner = inner;

        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct) => _inner.ProbeAsync(url, ct);
        public Task DownloadToEndAsync(string url, Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
            => _inner.DownloadToEndAsync(url, onChunk, ct);

        public async Task DownloadRangeAsync(string url, long start, long end,
            Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Interlocked.Increment(ref FailedCalls);
                throw new TransportException(TransportFailureKind.Transient, "Simulated transient failure of one worker");
            }
            await _inner.DownloadRangeAsync(url, start, end, onChunk, ct);
        }
    }

    /// <summary>Counts concurrent range requests.</summary>
    private sealed class ConcurrencyCountingTransport : ITransport
    {
        private readonly ITransport _inner;
        private int _inFlight;
        public int MaxConcurrent;
        public int TotalCalls;

        public ConcurrencyCountingTransport(ITransport inner) => _inner = inner;

        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct) => _inner.ProbeAsync(url, ct);
        public Task DownloadToEndAsync(string url, Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
            => _inner.DownloadToEndAsync(url, onChunk, ct);

        public async Task DownloadRangeAsync(string url, long start, long end,
            Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
        {
            Interlocked.Increment(ref TotalCalls);
            var current = Interlocked.Increment(ref _inFlight);
            int max;
            do { max = MaxConcurrent; }
            while (current > max && Interlocked.CompareExchange(ref MaxConcurrent, current, max) != max);
            try
            {
                await _inner.DownloadRangeAsync(url, start, end, onChunk, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}

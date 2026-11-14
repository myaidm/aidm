using AiDownloadManager.Core;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// Single-connection (no-Range) retry and recovery tests. These cover
/// the Phase 0 gap: the fallback path had no retry at all. The fallback
/// resumes by re-requesting the whole file and skipping the bytes already
/// safely on disk; the final length/hash check still applies, so a server
/// that returns different content on retry fails safely.
/// </summary>
public class SingleConnectionRetryTests : IAsyncLifetime
{
    private string _workDir = string.Empty;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aidm-retry-" + Guid.NewGuid().ToString("N")[..8]);
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

    private IDownloadJournal CreateJournal(string? name = null)
    {
        var journal = new SqliteDownloadJournal(
            Path.Combine(_workDir, name ?? $"journal-{Guid.NewGuid():N}.db"));
        journal.InitializeAsync().GetAwaiter().GetResult();
        return journal;
    }

    private static SegmentedDownloader CreateDownloader(
        ITransport transport, IDownloadJournal journal,
        SegmentedDownloaderOptions? options = null)
        => new(transport, journal, options: options ?? new SegmentedDownloaderOptions());

    [Fact]
    public async Task NoRangeServer_DropsConnection_RetrySucceeds_AndMatchesHash()
    {
        const long size = 5 * 1024 * 1024;
        // Probe consumes connection 1 (dropped harmlessly: the probe only
        // reads headers); the first download attempt is connection 2 and
        // is dropped after size/4; the retry (connection 3) succeeds.
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.IgnoreRange,
                DropAfterBytes: size / 4, DropFirstNConnections: 2));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await CreateDownloader(new HttpTransport(client), journal).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.True(outcome.UsedRangeFallback);
        Assert.Equal(sha, outcome.Sha256);
        Assert.Equal(size, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task NoRangeServer_RetryLimitReached_FailsSafelyWithoutPromoting()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.IgnoreRange,
                DropAfterBytes: size / 4, DropFirstNConnections: 100));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        var options = new SegmentedDownloaderOptions(
            MaxAttemptsPerRange: 2, BaseRetryDelay: TimeSpan.FromMilliseconds(10));

        var outcome = await CreateDownloader(new HttpTransport(client), journal, options).DownloadAsync(job);

        Assert.False(outcome.Success, "Every attempt was dropped; the download must fail, not loop forever");
        Assert.NotNull(outcome.ErrorMessage);
        Assert.False(File.Exists(destination), "A failed download must never be promoted");
        Assert.True(File.Exists(destination + ".part"), "The partial file is kept for diagnosis/resume");
    }

    [Fact]
    public async Task NoRangeServer_RetryLimitThenServerRecovers_ResumeSucceeds()
    {
        const long size = 5 * 1024 * 1024;
        // Connection counting is exact: each DownloadAsync call makes one
        // probe connection plus one connection per download attempt.
        // Run 1: probe (1) + 3 attempts (2,3,4) — all dropped (limit 6).
        // Run 2: probe (5, dropped harmlessly) + attempt (6, dropped) +
        // retry (7, not dropped) — succeeds.
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.IgnoreRange,
                DropAfterBytes: size / 4, DropFirstNConnections: 6));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var dbPath = Path.Combine(_workDir, "shared.db");
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        var options = new SegmentedDownloaderOptions(
            MaxAttemptsPerRange: 2, BaseRetryDelay: TimeSpan.FromMilliseconds(10));

        var journal1 = new SqliteDownloadJournal(dbPath);
        await journal1.InitializeAsync();
        var first = await new SegmentedDownloader(new HttpTransport(client), journal1, options: options)
            .DownloadAsync(job);
        Assert.False(first.Success);

        var journal2 = new SqliteDownloadJournal(dbPath);
        await journal2.InitializeAsync();
        var second = await new SegmentedDownloader(new HttpTransport(client), journal2, options: options)
            .DownloadAsync(job with { State = DownloadState.Queued });
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(sha, second.Sha256);
    }

    [Fact]
    public async Task NoRangeServer_CancelledMidway_ResumesFromPrefix_AfterRestart()
    {
        const long size = 10 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.IgnoreRange,
                ThrottleBytesPerSecond: 2 * 1024 * 1024));
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
                if (p.TotalBytes is not null && p.DownloadedBytes * 100 / p.TotalBytes.Value >= 30)
                    cts.Cancel();
            });
            var first = await new SegmentedDownloader(new HttpTransport(client), journal1)
                .DownloadAsync(job, progress, cts.Token);
            Assert.True(first.Cancelled, "The first run should have been cancelled");
            Assert.True(first.BytesDownloaded > 0);
            Assert.False(File.Exists(destination), "A cancelled download must not be promoted");
        }

        // The journal must hold a resumable prefix [0, N-1], not nothing.
        var journal2 = new SqliteDownloadJournal(dbPath);
        await journal2.InitializeAsync();
        var resumed = await journal2.LoadCompletedRangesAsync(job.Id);
        Assert.NotEmpty(resumed);
        Assert.Equal(0, resumed[0].Start);
        Assert.True(resumed[0].End > 0, "The prefix should cover more than the first chunk");

        // A brand-new engine instance resumes from that prefix.
        var second = await new SegmentedDownloader(new HttpTransport(client), journal2)
            .DownloadAsync(job with { State = DownloadState.Queued });
        Assert.True(second.Success, second.ErrorMessage);
        Assert.True(second.UsedRangeFallback);
        Assert.Equal(sha, second.Sha256);
    }

    [Fact]
    public async Task CancellationDuringRetryDelay_IsReportedAsCancelled()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.IgnoreRange,
                DropAfterBytes: size / 4, DropFirstNConnections: 100));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        // A 10 s retry delay: the cancel below must land inside it.
        var options = new SegmentedDownloaderOptions(
            MaxAttemptsPerRange: 5, BaseRetryDelay: TimeSpan.FromSeconds(10));
        var signalling = new SignallingTransport(new HttpTransport(client));
        using var cts = new CancellationTokenSource();

        var downloadTask = new SegmentedDownloader(signalling, journal, options: options)
            .DownloadAsync(job, cancellationToken: cts.Token);
        // Wait until the first attempt has failed and the engine is
        // sitting in its retry delay, then cancel.
        await signalling.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();

        var outcome = await downloadTask;
        Assert.True(outcome.Cancelled, "Cancelling during the retry delay must report Cancelled, not Failed");
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task NoRangeServer_ReturnsFewerBytesOnRetry_FailsSafelyWithoutPromoting()
    {
        const long size = 5 * 1024 * 1024;
        // First attempt is dropped by the server after size/4 (transient,
        // so the engine retries). The second attempt is truncated by the
        // wrapper to 256 KB: the server now returns fewer bytes than are
        // already on disk, which must fail permanently, not loop and not
        // promote a short file.
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.IgnoreRange,
                DropAfterBytes: size / 4, DropFirstNConnections: 2));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        var options = new SegmentedDownloaderOptions(
            MaxAttemptsPerRange: 5, BaseRetryDelay: TimeSpan.FromMilliseconds(10));
        var truncating = new TruncatingTransport(new HttpTransport(client), truncateAfterBytes: 256 * 1024);

        var outcome = await new SegmentedDownloader(truncating, journal, options: options)
            .DownloadAsync(job);

        Assert.False(outcome.Success);
        Assert.Contains("fewer bytes", outcome.ErrorMessage);
        Assert.False(File.Exists(destination), "A truncated download must never be promoted");
    }

    /// <summary>Signals the first transient failure of the inner transport.</summary>
    private sealed class SignallingTransport : ITransport
    {
        private readonly ITransport _inner;
        public TaskCompletionSource FirstFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SignallingTransport(ITransport inner) => _inner = inner;

        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
            => _inner.ProbeAsync(url, ct);

        public Task DownloadRangeAsync(string url, long start, long end,
            Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
            => _inner.DownloadRangeAsync(url, start, end, onChunk, ct);

        public async Task DownloadToEndAsync(string url,
            Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
        {
            try
            {
                await _inner.DownloadToEndAsync(url, onChunk, ct);
            }
            catch (TransportException ex) when (ex.Kind == TransportFailureKind.Transient)
            {
                FirstFailure.TrySetResult();
                throw;
            }
        }
    }

    /// <summary>
    /// Forwards only the first <see cref="_truncateAfterBytes"/> bytes of
    /// the SECOND and later DownloadToEndAsync calls, then ends the stream
    /// cleanly — modelling a server whose content shrank between attempts.
    /// </summary>
    private sealed class TruncatingTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly long _truncateAfterBytes;
        private int _calls;

        public TruncatingTransport(ITransport inner, long truncateAfterBytes)
        {
            _inner = inner;
            _truncateAfterBytes = truncateAfterBytes;
        }

        public Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
            => _inner.ProbeAsync(url, ct);

        public Task DownloadRangeAsync(string url, long start, long end,
            Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
            => _inner.DownloadRangeAsync(url, start, end, onChunk, ct);

        public async Task DownloadToEndAsync(string url,
            Func<ReadOnlyMemory<byte>, long, Task> onChunk, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                await _inner.DownloadToEndAsync(url, onChunk, ct);
                return;
            }
            long forwarded = 0;
            await _inner.DownloadToEndAsync(url, async (chunk, offset) =>
            {
                if (forwarded >= _truncateAfterBytes)
                    return; // swallow the rest: the stream ends early
                var take = (int)Math.Min(chunk.Length, _truncateAfterBytes - forwarded);
                forwarded += take;
                await onChunk(chunk[..take], offset);
            }, ct);
        }
    }
}

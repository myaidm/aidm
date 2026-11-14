using AiDownloadManager.Core;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// Engine integration tests against the deterministic test server.
/// The server listens on a Unix domain socket (not TCP loopback):
/// this sandbox blocks .NET TCP connections, but the engine is
/// transport-agnostic, so the same code paths are exercised. No test
/// touches the public internet. Files are small (5–20 MB) so the
/// suite stays fast; the 1 GB acceptance run is the Prototype's job,
/// not the unit suite's.
/// </summary>
public class EngineIntegrationTests : IAsyncLifetime
{
    private string _workDir = string.Empty;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aidm-tests-" + Guid.NewGuid().ToString("N")[..8]);
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
        // Unix socket per server, inside the test work dir.
        options = options with
        {
            UnixSocketPath = Path.Combine(_workDir, $"srv-{Guid.NewGuid():N}.sock")
        };
        var server = new DeterministicTestServer(source, options);
        await server.StartAsync();
        return (server, sha);
    }

    private static SegmentedDownloader CreateDownloader(
        IDownloadJournal journal, HttpClient client, int connections = 4)
    {
        return new SegmentedDownloader(
            new HttpTransport(client),
            journal,
            options: new SegmentedDownloaderOptions(MaxConnections: connections));
    }

    private IDownloadJournal CreateJournal()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, $"journal-{Guid.NewGuid():N}.db"));
        journal.InitializeAsync().GetAwaiter().GetResult();
        return journal;
    }

    [Fact]
    public async Task DownloadsSmallFile_MatchesSourceHash()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size, new TestServerOptions(size));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await CreateDownloader(journal, client).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
        Assert.Equal(size, new FileInfo(destination).Length);
        Assert.False(outcome.UsedRangeFallback);
    }

    [Fact]
    public async Task NoRangeServer_FallsBackToSingleConnection_AndStillMatchesHash()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.IgnoreRange));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await CreateDownloader(journal, client).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.True(outcome.UsedRangeFallback);
        Assert.Equal(sha, outcome.Sha256);
    }

    [Fact]
    public async Task InvalidRangeResponse_FailsSafelyWithoutPromotingAFile()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.InvalidRange));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await CreateDownloader(journal, client).DownloadAsync(job);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.ErrorMessage);
        Assert.False(File.Exists(destination), "A corrupt file must never be promoted to the destination");
    }

    [Fact]
    public async Task InconsistentContentRange_FailsSafelyWithoutPromotingAFile()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.InconsistentContentRange));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await CreateDownloader(journal, client).DownloadAsync(job);

        Assert.False(outcome.Success);
        Assert.False(File.Exists(destination), "A corrupt file must never be promoted to the destination");
    }

    [Fact]
    public async Task DroppedConnections_AreRetried_AndDownloadCompletes()
    {
        const long size = 10 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, Behavior: TestServerBehavior.DropConnection,
                DropAfterBytes: size / 4, DropFirstNConnections: 2));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await CreateDownloader(journal, client).DownloadAsync(job);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
    }

    [Fact]
    public async Task CancelledDownload_ResumesFromJournal_AndMatchesHash()
    {
        const long size = 20 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, ThrottleBytesPerSecond: 2 * 1024 * 1024));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, sha, DownloadState.Queued, DateTimeOffset.UtcNow);
        var downloader = CreateDownloader(journal, client);

        // Cancel once ~30% is done, then resume with the same journal.
        using var cts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.TotalBytes is not null && p.DownloadedBytes * 100 / p.TotalBytes.Value >= 30)
                cts.Cancel();
        });
        var first = await downloader.DownloadAsync(job, progress, cts.Token);
        Assert.True(first.Cancelled, "The first run should have been cancelled");
        Assert.True(first.BytesDownloaded > 0, "Some bytes should have been journaled before cancellation");
        Assert.False(File.Exists(destination), "A cancelled download must not be promoted");

        var second = await downloader.DownloadAsync(job with { State = DownloadState.Queued });
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(sha, second.Sha256);
    }

    [Fact]
    public async Task JournalSurvivesNewEngineInstance_ResumeWorks()
    {
        const long size = 10 * 1024 * 1024;
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
                if (p.TotalBytes is not null && p.DownloadedBytes * 100 / p.TotalBytes.Value >= 25)
                    cts.Cancel();
            });
            var first = await new SegmentedDownloader(new HttpTransport(client), journal1)
                .DownloadAsync(job, progress, cts.Token);
            Assert.True(first.Cancelled);
        }

        // A brand-new engine and journal instance resumes the same job.
        var journal2 = new SqliteDownloadJournal(dbPath);
        await journal2.InitializeAsync();
        var resumed = await journal2.LoadCompletedRangesAsync(job.Id);
        Assert.NotEmpty(resumed);
        var second = await new SegmentedDownloader(new HttpTransport(client), journal2)
            .DownloadAsync(job with { State = DownloadState.Queued });
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(sha, second.Sha256);
    }

    [Fact]
    public async Task WrongExpectedHash_FailsVerification_AndDoesNotPromote()
    {
        const long size = 5 * 1024 * 1024;
        var (server, _) = await StartServerAsync(size, new TestServerOptions(size));
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var destination = Path.Combine(_workDir, "out.bin");
        var wrongHash = new string('0', 64);
        var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, size, wrongHash, DownloadState.Queued, DateTimeOffset.UtcNow);

        var outcome = await CreateDownloader(journal, client).DownloadAsync(job);

        Assert.False(outcome.Success);
        Assert.Contains("SHA-256 mismatch", outcome.ErrorMessage);
        Assert.False(File.Exists(destination));
    }
}

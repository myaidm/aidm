using AiDownloadManager.Application;
using AiDownloadManager.Core;
using AiDownloadManager.Engine;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// Job-handle API tests for <see cref="DownloadService"/> (Phase 1
/// closure): starting returns a handle with the job id immediately,
/// pause / resume / cancel address a running download by id, and the
/// journal list / delete operations make history survive a restart.
/// All downloads run against the local deterministic test server; no
/// test touches the public internet.
/// </summary>
public class DownloadServiceTests : IAsyncLifetime
{
    private string _workDir = string.Empty;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aidm-svc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private async Task<(DeterministicTestServer Server, string Sha256)> StartServerAsync(
        long sizeBytes, TestServerOptions? options = null)
    {
        var source = Path.Combine(_workDir, $"source-{Guid.NewGuid():N}.bin");
        var sha = TestDataGenerator.Generate(source, sizeBytes, seed: 7);
        var server = new DeterministicTestServer(source, options ?? new TestServerOptions(sizeBytes)
        {
            UnixSocketPath = Path.Combine(_workDir, $"srv-{Guid.NewGuid():N}.sock")
        });
        await server.StartAsync();
        return (server, sha);
    }

    private DownloadService CreateService(SqliteDownloadJournal journal, HttpClient client)
        => new(journal, opts => new SegmentedDownloader(
            new HttpTransport(client), journal, options: opts));

    private static DownloadJob NewJob(string url, string destination, long? totalBytes, string? sha)
        => new(Guid.NewGuid(), url, destination, totalBytes, sha,
            DownloadState.Queued, DateTimeOffset.UtcNow);

    [Fact]
    public async Task StartDownloadAsync_ReturnsHandle_AndCompletesWithMatchingHash()
    {
        const long size = 1024 * 1024;
        var (server, sha) = await StartServerAsync(size);
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "svc.db"));
        await journal.InitializeAsync();
        var service = CreateService(journal, client);
        var destination = Path.Combine(_workDir, "out.bin");
        var job = NewJob(server.FileUrl, destination, size, sha);

        var handle = await service.StartDownloadAsync(job, maxConnections: 4);

        // The job id is available immediately, and the job is already
        // in the journal before the download finishes.
        Assert.Equal(job.Id, handle.JobId);
        Assert.NotNull(await journal.LoadJobAsync(job.Id));
        var outcome = await handle.Completion;
        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task PauseThenResume_CompletesWithMatchingHash()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, ThrottleBytesPerSecond: 1024 * 1024)
            {
                UnixSocketPath = Path.Combine(_workDir, $"srv-{Guid.NewGuid():N}.sock")
            });
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "svc.db"));
        await journal.InitializeAsync();
        var service = CreateService(journal, client);
        var destination = Path.Combine(_workDir, "out.bin");
        var job = NewJob(server.FileUrl, destination, size, sha);

        var started = new TaskCompletionSource();
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.DownloadedBytes > 0)
                started.TrySetResult();
        });
        var handle = await service.StartDownloadAsync(job, progress, maxConnections: 2);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await service.PauseAsync(job.Id);
        var paused = await handle.Completion;
        Assert.True(paused.Cancelled, "pausing must report the download as cancelled");
        Assert.False(paused.Success);
        // The partial file and the journal ranges survive the pause.
        Assert.True(File.Exists(destination + ".part"));
        Assert.NotEmpty(await journal.LoadCompletedRangesAsync(job.Id));

        var resumed = await service.ResumeDownloadAsync(job.Id, maxConnections: 2);
        var outcome = await resumed.Completion;
        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Equal(sha, outcome.Sha256);
    }

    [Fact]
    public async Task CancelAsync_DiscardsPartial_AndClearsRanges()
    {
        const long size = 5 * 1024 * 1024;
        var (server, sha) = await StartServerAsync(size,
            new TestServerOptions(size, ThrottleBytesPerSecond: 1024 * 1024)
            {
                UnixSocketPath = Path.Combine(_workDir, $"srv-{Guid.NewGuid():N}.sock")
            });
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "svc.db"));
        await journal.InitializeAsync();
        var service = CreateService(journal, client);
        var destination = Path.Combine(_workDir, "out.bin");
        var job = NewJob(server.FileUrl, destination, size, sha);

        var started = new TaskCompletionSource();
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.DownloadedBytes > 0)
                started.TrySetResult();
        });
        var handle = await service.StartDownloadAsync(job, progress, maxConnections: 2);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await service.CancelAsync(job.Id);
        var outcome = await handle.Completion;
        Assert.True(outcome.Cancelled);

        // The partial download is gone: no ranges, no .part file, and
        // the job is recorded as cancelled.
        Assert.Empty(await journal.LoadCompletedRangesAsync(job.Id));
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination));
        Assert.Equal(DownloadState.Cancelled, (await journal.LoadJobAsync(job.Id))!.State);
    }

    [Fact]
    public async Task ListJobs_AndDeleteJob_RoundTrip()
    {
        const long size = 256 * 1024;
        var (server, sha) = await StartServerAsync(size);
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "svc.db"));
        await journal.InitializeAsync();
        var service = CreateService(journal, client);

        var job1 = NewJob(server.FileUrl, Path.Combine(_workDir, "one.bin"), size, sha);
        var job2 = NewJob(server.FileUrl, Path.Combine(_workDir, "two.bin"), size, sha);
        Assert.True((await (await service.StartDownloadAsync(job1)).Completion).Success);
        Assert.True((await (await service.StartDownloadAsync(job2)).Completion).Success);

        // Both jobs are listed, and a fresh service instance over the
        // same journal sees them too (history survives a restart).
        Assert.Equal(new[] { job1.Id, job2.Id },
            (await service.ListJobsAsync()).Select(j => j.Id));
        var reopened = CreateService(journal, client);
        Assert.Equal(2, (await reopened.ListJobsAsync()).Count);

        await service.DeleteJobAsync(job1.Id);
        Assert.Null(await journal.LoadJobAsync(job1.Id));
        Assert.Equal(new[] { job2.Id },
            (await service.ListJobsAsync()).Select(j => j.Id));
    }

    [Fact]
    public async Task ResumeDownloadAsync_UnknownId_Throws()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "svc.db"));
        await journal.InitializeAsync();
        using var client = new HttpClient();
        var service = CreateService(journal, client);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ResumeDownloadAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task StartDownloadAsync_PerDownloadConnections_AreHonored()
    {
        const long size = 1024 * 1024;
        var (server, sha) = await StartServerAsync(size);
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "svc.db"));
        await journal.InitializeAsync();
        var service = CreateService(journal, client);

        // The factory path is exercised with different connection
        // counts; both complete with a matching hash.
        var one = NewJob(server.FileUrl, Path.Combine(_workDir, "c1.bin"), size, sha);
        var four = NewJob(server.FileUrl, Path.Combine(_workDir, "c4.bin"), size, sha);
        Assert.Equal(sha, (await (await service.StartDownloadAsync(one, maxConnections: 1)).Completion).Sha256);
        Assert.Equal(sha, (await (await service.StartDownloadAsync(four, maxConnections: 4)).Completion).Sha256);
    }
}

using AiDownloadManager.Core;
using AiDownloadManager.Infrastructure;

namespace AiDownloadManager.Engine.Tests;

public class JournalTests : IAsyncLifetime
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

    [Fact]
    public async Task SaveAndLoadJob_RoundTrips()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "j.db"));
        await journal.InitializeAsync();
        var job = new DownloadJob(Guid.NewGuid(), "http://127.0.0.1/file.bin",
            Path.Combine(_workDir, "out.bin"), 12345, "abc", DownloadState.Queued, DateTimeOffset.UtcNow);

        await journal.SaveJobAsync(job);
        var loaded = await journal.LoadJobAsync(job.Id);

        Assert.NotNull(loaded);
        Assert.Equal(job.Url, loaded!.Url);
        Assert.Equal(job.DestinationPath, loaded.DestinationPath);
        Assert.Equal(12345, loaded.TotalBytes);
        Assert.Equal("abc", loaded.ExpectedSha256);
    }

    [Fact]
    public async Task CompletedRanges_RoundTrip_AndAreMergedOnRead()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "j.db"));
        await journal.InitializeAsync();
        var id = Guid.NewGuid();

        await journal.SaveCompletedRangesAsync(id, new[]
        {
            new ByteRange(500, 600),
            new ByteRange(0, 100),
            new ByteRange(90, 200)
        });
        var loaded = await journal.LoadCompletedRangesAsync(id);

        // Stored merged: [0,200] and [500,600].
        Assert.Equal(new[] { new ByteRange(0, 200), new ByteRange(500, 600) }, loaded);
    }

    [Fact]
    public async Task SaveCompletedRanges_ReplacesPreviousSet()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "j.db"));
        await journal.InitializeAsync();
        var id = Guid.NewGuid();

        await journal.SaveCompletedRangesAsync(id, new[] { new ByteRange(0, 100) });
        await journal.SaveCompletedRangesAsync(id, new[] { new ByteRange(0, 500) });

        var loaded = await journal.LoadCompletedRangesAsync(id);
        Assert.Equal(new[] { new ByteRange(0, 500) }, loaded);
    }

    [Fact]
    public async Task LoadMissingJob_ReturnsNull()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "j.db"));
        await journal.InitializeAsync();
        Assert.Null(await journal.LoadJobAsync(Guid.NewGuid()));
        Assert.Empty(await journal.LoadCompletedRangesAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Initialize_IsIdempotent_AcrossInstances()
    {
        var path = Path.Combine(_workDir, "j.db");
        var first = new SqliteDownloadJournal(path);
        await first.InitializeAsync();
        var second = new SqliteDownloadJournal(path);
        await second.InitializeAsync(); // must not throw or duplicate migrations
        var job = new DownloadJob(Guid.NewGuid(), "http://127.0.0.1/x", Path.Combine(_workDir, "o.bin"),
            null, null, DownloadState.Queued, DateTimeOffset.UtcNow);
        await second.SaveJobAsync(job);
        Assert.NotNull(await first.LoadJobAsync(job.Id));
    }

    [Fact]
    public async Task ListJobs_ReturnsAllJobsOldestFirst()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "j.db"));
        await journal.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var jobs = new[]
        {
            new DownloadJob(Guid.NewGuid(), "http://127.0.0.1/1", Path.Combine(_workDir, "1.bin"), 1, null, DownloadState.Queued, now.AddMinutes(-2)),
            new DownloadJob(Guid.NewGuid(), "http://127.0.0.1/2", Path.Combine(_workDir, "2.bin"), 2, null, DownloadState.Queued, now.AddMinutes(-1)),
            new DownloadJob(Guid.NewGuid(), "http://127.0.0.1/3", Path.Combine(_workDir, "3.bin"), 3, null, DownloadState.Queued, now),
        };
        foreach (var job in jobs)
            await journal.SaveJobAsync(job);

        var listed = await journal.ListJobsAsync();

        Assert.Equal(jobs.Select(j => j.Id), listed.Select(j => j.Id));
        Assert.Equal("http://127.0.0.1/2", listed[1].Url);
    }

    [Fact]
    public async Task DeleteJob_RemovesJobAndItsRanges()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "j.db"));
        await journal.InitializeAsync();
        var keep = new DownloadJob(Guid.NewGuid(), "http://127.0.0.1/keep", Path.Combine(_workDir, "keep.bin"), 10, null, DownloadState.Queued, DateTimeOffset.UtcNow);
        var drop = new DownloadJob(Guid.NewGuid(), "http://127.0.0.1/drop", Path.Combine(_workDir, "drop.bin"), 10, null, DownloadState.Queued, DateTimeOffset.UtcNow);
        await journal.SaveJobAsync(keep);
        await journal.SaveJobAsync(drop);
        await journal.SaveCompletedRangesAsync(drop.Id, new[] { new ByteRange(0, 5) });

        await journal.DeleteJobAsync(drop.Id);

        Assert.Null(await journal.LoadJobAsync(drop.Id));
        Assert.Empty(await journal.LoadCompletedRangesAsync(drop.Id));
        Assert.Equal(new[] { keep.Id }, (await journal.ListJobsAsync()).Select(j => j.Id));
    }

    [Fact]
    public async Task DeleteUnknownJob_IsNoOp()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, "j.db"));
        await journal.InitializeAsync();
        await journal.DeleteJobAsync(Guid.NewGuid()); // must not throw
        Assert.Empty(await journal.ListJobsAsync());
    }
}

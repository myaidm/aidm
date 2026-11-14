using System.Diagnostics;
using System.Text.Json;
using AiDownloadManager.Core;
using AiDownloadManager.Engine;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;

/// <summary>
/// Repeatable benchmark harness for Ai Download Manager. Compares the
/// engine against itself (connection counts, file sizes, resume, retry)
/// and, when an aria2 binary is available, against aria2 on the same
/// local deterministic server and the same bytes. Nothing here touches
/// the public internet.
///
/// Usage:
///   benchmark [--size-mb N] [--connections 1,4,8,16] [--trials N]
///             [--work-dir PATH] [--mode normal|drop] [--throttle BYTES_PER_SEC]
///             [--aria2 PATH] [--interrupt-at PERCENT] [--resume-only]
///             [--timing end-to-end|split] [--report PATH]
/// --aria2 runs aria2c (if the binary exists) against a TCP-loopback
/// instance of the same test server; the engine always uses the Unix
/// socket instance (this sandbox blocks .NET TCP). Both serve the
/// identical deterministic file, so SHA-256 comparison is meaningful.
/// --timing split records download-only and end-to-end times
/// separately for both tools (see the fairness note below), so the
/// comparison is apples-to-apples instead of timing one tool's
/// transfer against the other's transfer+verification.
/// Results are printed as a table and written as JSON to --report.
///
/// Fairness note: the engine's DownloadAsync verifies SHA-256 and
/// flushes the journal before it returns, so its wall time is an
/// end-to-end number; aria2's process time is download-only (its
/// hash is checked after the process exits). In --timing split mode
/// each tool reports both boundaries: download-only (transfer
/// complete) and end-to-end (transfer + SHA-256 verification +
/// journal/promotion for the engine; transfer + SHA-256 verification
/// for aria2). The legacy end-to-end numbers are kept unchanged in
/// BENCHMARKS.md; the split numbers live alongside them.
/// </summary>
var sizeMb = ArgInt("--size-mb", 100);
var connections = ArgString("--connections", "1,4,8,16")
    .Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(int.Parse).ToArray();
var trials = ArgInt("--trials", 3);
var workDir = ArgString("--work-dir", Path.Combine(Path.GetTempPath(), "aidm-bench"));
var mode = ArgString("--mode", "normal");
var throttle = ArgInt("--throttle", 0);
var aria2Path = ArgStringOrNull("--aria2");
var interruptAt = ArgInt("--interrupt-at", -1);
var timing = ArgString("--timing", "end-to-end");
if (timing is not ("end-to-end" or "split"))
    throw new ArgumentException($"Unknown --timing: {timing} (expected end-to-end or split)");
var splitTiming = timing == "split";
var reportPath = ArgStringOrNull("--report")
    ?? Path.Combine(workDir, "benchmark-report.json");

Directory.CreateDirectory(workDir);
var sizeBytes = (long)sizeMb * 1024 * 1024;
var sourcePath = Path.Combine(workDir, $"source-{sizeMb}mb.bin");
string sourceSha;
if (File.Exists(sourcePath) && new FileInfo(sourcePath).Length == sizeBytes)
{
    sourceSha = TestDataGenerator.ComputeSha256(sourcePath);
    Console.WriteLine($"[bench] reusing source file {sizeMb} MB (sha256 {sourceSha[..16]}…)");
}
else
{
    sourceSha = TestDataGenerator.Generate(sourcePath, sizeBytes, seed: 42);
    Console.WriteLine($"[bench] generated {sizeMb} MB source (sha256 {sourceSha})");
}

var behavior = mode switch
{
    "normal" => TestServerBehavior.Normal,
    "drop" => TestServerBehavior.DropConnection,
    _ => throw new ArgumentException($"Unknown mode: {mode}")
};

var results = new List<BenchmarkResult>();

// Engine runs: one Unix-socket server, reused across trials.
var engineOptions = new TestServerOptions(
    FileSizeBytes: sizeBytes,
    Behavior: behavior,
    ThrottleBytesPerSecond: throttle,
    DropAfterBytes: behavior == TestServerBehavior.DropConnection ? sizeBytes / 4 : 0,
    DropFirstNConnections: 2,
    UnixSocketPath: Path.Combine(workDir, "bench-server.sock"));
await using var engineServer = new DeterministicTestServer(sourcePath, engineOptions);
await engineServer.StartAsync();

foreach (var conn in connections)
{
    for (var trial = 1; trial <= trials; trial++)
    {
        // Split timing is a throughput comparison mode; the interrupt
        // path is a resume test and keeps the legacy single timing.
        var result = splitTiming && interruptAt < 0
            ? await RunEngineTrialSplitAsync(conn, trial)
            : await RunEngineTrialAsync(conn, trial);
        results.Add(result);
        Console.WriteLine($"[bench] engine conns={conn} trial={trial}: {result.DurationSeconds:F2}s " +
            $"{result.ThroughputMBs:F1} MB/s sha-ok={result.ShaOk} journal-writes={result.JournalWrites} " +
            $"peak-rss={result.PeakWorkingSetMB:F0} MB cpu={result.CpuSeconds:F1}s" +
            (splitTiming && result.DownloadOnlySeconds is not null
                ? $" download-only={result.DownloadOnlySeconds:F2}s ({sizeBytes / Math.Max(result.DownloadOnlySeconds.Value, 0.01) / 1048576.0:F1} MB/s)"
                : ""));
    }
}

// aria2 comparison (optional): a separate TCP-loopback server instance
// serving the same file. aria2 is a native binary, so loopback TCP works
// for it even though .NET TCP is blocked in this sandbox.
if (aria2Path is not null)
{
    if (!File.Exists(aria2Path))
    {
        Console.WriteLine($"[bench] aria2 not found at {aria2Path}; skipping comparison.");
    }
    else
    {
        var tcpOptions = new TestServerOptions(
            FileSizeBytes: sizeBytes, Behavior: behavior,
            ThrottleBytesPerSecond: throttle,
            DropAfterBytes: behavior == TestServerBehavior.DropConnection ? sizeBytes / 4 : 0,
            DropFirstNConnections: 2);
        await using var tcpServer = new DeterministicTestServer(sourcePath, tcpOptions);
        await tcpServer.StartAsync();
        Console.WriteLine($"[bench] aria2 server at {tcpServer.BaseUrl} (TCP loopback)");
        foreach (var conn in connections)
        {
            for (var trial = 1; trial <= trials; trial++)
            {
                var result = await RunAria2TrialAsync(tcpServer, conn, trial);
                results.Add(result);
                Console.WriteLine($"[bench] aria2  conns={conn} trial={trial}: {result.DurationSeconds:F2}s " +
                    $"{result.ThroughputMBs:F1} MB/s sha-ok={result.ShaOk} peak-rss={result.PeakWorkingSetMB:F0} MB cpu={result.CpuSeconds:F1}s" +
                    (splitTiming && result.DownloadOnlySeconds is not null
                        ? $" download-only={result.DownloadOnlySeconds:F2}s ({sizeBytes / Math.Max(result.DownloadOnlySeconds.Value, 0.01) / 1048576.0:F1} MB/s)"
                        : ""));
            }
        }
    }
}

var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(reportPath, json);
Console.WriteLine($"[bench] report written to {reportPath}");
PrintSummary();
return 0;

async Task<BenchmarkResult> RunEngineTrialAsync(int connCount, int trial)
{
    var dbPath = Path.Combine(workDir, $"bench-{connCount}-{trial}.db");
    var destination = Path.Combine(workDir, $"bench-engine-{connCount}-{trial}.bin");
    try { File.Delete(destination); } catch { }
    try { File.Delete(destination + ".part"); } catch { }
    var journal = new SqliteDownloadJournal(dbPath);
    await journal.InitializeAsync();
    var counting = new CountingJournal(journal);
    using var client = engineServer.CreateHttpClient();
    var downloader = new SegmentedDownloader(
        new HttpTransport(client), counting,
        options: new SegmentedDownloaderOptions(MaxConnections: connCount));
    var job = new DownloadJob(Guid.NewGuid(), engineServer.FileUrl, destination,
        sizeBytes, sourceSha, DownloadState.Queued, DateTimeOffset.UtcNow);

    var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var wall = Stopwatch.StartNew();
    long peakRss = 0;
    DownloadOutcome outcome;
    if (interruptAt >= 0)
    {
        using var cts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(p =>
        {
            peakRss = Math.Max(peakRss, Process.GetCurrentProcess().WorkingSet64);
            if (p.TotalBytes is not null && p.DownloadedBytes * 100 / p.TotalBytes.Value >= interruptAt)
                cts.Cancel();
        });
        outcome = await downloader.DownloadAsync(job, progress, cts.Token);
        if (!outcome.Cancelled)
            throw new InvalidOperationException("Expected the trial run to be cancelled");
        var resumeWall = Stopwatch.StartNew();
        outcome = await downloader.DownloadAsync(job with { State = DownloadState.Queued });
        resumeWall.Stop();
        wall.Stop();
        var cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;
        return new BenchmarkResult("engine", connCount, trial, sizeBytes,
            wall.Elapsed.TotalSeconds, resumeWall.Elapsed.TotalSeconds,
            outcome.BytesDownloaded / Math.Max(wall.Elapsed.TotalSeconds, 0.01) / 1048576.0,
            outcome.Sha256 == sourceSha, counting.Writes,
            peakRss / 1048576.0, (cpuAfter - cpuBefore).TotalSeconds, outcome.Success);
    }
    outcome = await downloader.DownloadAsync(job);
    wall.Stop();
    var cpuAfter2 = Process.GetCurrentProcess().TotalProcessorTime;
    return new BenchmarkResult("engine", connCount, trial, sizeBytes,
        wall.Elapsed.TotalSeconds, null,
        outcome.BytesDownloaded / Math.Max(wall.Elapsed.TotalSeconds, 0.01) / 1048576.0,
        outcome.Sha256 == sourceSha, counting.Writes,
        Process.GetCurrentProcess().WorkingSet64 / 1048576.0,
        (cpuAfter2 - cpuBefore).TotalSeconds, outcome.Success);
}

async Task<BenchmarkResult> RunEngineTrialSplitAsync(int connCount, int trial)
{
    var dbPath = Path.Combine(workDir, $"bench-{connCount}-{trial}.db");
    var destination = Path.Combine(workDir, $"bench-engine-{connCount}-{trial}.bin");
    try { File.Delete(destination); } catch { }
    try { File.Delete(destination + ".part"); } catch { }
    var journal = new SqliteDownloadJournal(dbPath);
    await journal.InitializeAsync();
    var counting = new CountingJournal(journal);
    using var client = engineServer.CreateHttpClient();
    var downloader = new SegmentedDownloader(
        new HttpTransport(client), counting,
        options: new SegmentedDownloaderOptions(MaxConnections: connCount));
    var job = new DownloadJob(Guid.NewGuid(), engineServer.FileUrl, destination,
        sizeBytes, sourceSha, DownloadState.Queued, DateTimeOffset.UtcNow);

    var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var wall = Stopwatch.StartNew();
    // Download-only boundary: the first progress report where every
    // byte has arrived. Verification, journal flushes, and promotion
    // happen after it, inside DownloadAsync.
    double? downloadOnlySeconds = null;
    var progress = new Progress<DownloadProgress>(p =>
    {
        if (downloadOnlySeconds is null && p.TotalBytes is not null
            && p.DownloadedBytes >= p.TotalBytes.Value)
            downloadOnlySeconds = wall.Elapsed.TotalSeconds;
    });
    var outcome = await downloader.DownloadAsync(job, progress);
    wall.Stop();
    var cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;
    var endToEndSeconds = wall.Elapsed.TotalSeconds;
    return new BenchmarkResult("engine", connCount, trial, sizeBytes,
        endToEndSeconds, null,
        outcome.BytesDownloaded / Math.Max(endToEndSeconds, 0.01) / 1048576.0,
        outcome.Sha256 == sourceSha, counting.Writes,
        Process.GetCurrentProcess().WorkingSet64 / 1048576.0,
        (cpuAfter - cpuBefore).TotalSeconds, outcome.Success,
        DownloadOnlySeconds: downloadOnlySeconds ?? endToEndSeconds,
        EndToEndSeconds: endToEndSeconds);
}

async Task<BenchmarkResult> RunAria2TrialAsync(DeterministicTestServer tcpServer, int connCount, int trial)
{
    var destination = Path.Combine(workDir, $"bench-aria2-{connCount}-{trial}.bin");
    try { File.Delete(destination); } catch { }
    var startInfo = new ProcessStartInfo
    {
        FileName = aria2Path!,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("--allow-overwrite=true");
    startInfo.ArgumentList.Add("--auto-file-renaming=false");
    startInfo.ArgumentList.Add($"--max-connection-per-server={connCount}");
    startInfo.ArgumentList.Add($"--split={connCount}");
    startInfo.ArgumentList.Add($"--min-split-size=1M");
    startInfo.ArgumentList.Add($"--dir={workDir}");
    startInfo.ArgumentList.Add($"--out={Path.GetFileName(destination)}");
    startInfo.ArgumentList.Add(tcpServer.FileUrl);
    using var process = new Process { StartInfo = startInfo };
    var wall = Stopwatch.StartNew();
    process.Start();
    var peakRss = 0L;
    var cpuTime = TimeSpan.Zero; // last-seen CPU time while the child is alive (TotalProcessorTime throws after exit on Linux)
    var monitor = Task.Run(async () =>
    {
        try
        {
            while (!process.HasExited)
            {
                process.Refresh();
                peakRss = Math.Max(peakRss, process.WorkingSet64);
                cpuTime = process.TotalProcessorTime;
                await Task.Delay(50);
            }
        }
        catch { }
    });
    await process.WaitForExitAsync();
    await monitor;
    wall.Stop();
    var downloadOnlySeconds = wall.Elapsed.TotalSeconds;
    double endToEndSeconds = downloadOnlySeconds;
    bool shaOk;
    if (splitTiming)
    {
        // End-to-end for aria2 is download + SHA-256 verification of
        // the downloaded file (the same work the engine's wall time
        // already includes). Timed separately so both boundaries are
        // reported.
        var verify = Stopwatch.StartNew();
        shaOk = File.Exists(destination) && TestDataGenerator.ComputeSha256(destination) == sourceSha;
        verify.Stop();
        endToEndSeconds = downloadOnlySeconds + verify.Elapsed.TotalSeconds;
    }
    else
    {
        shaOk = File.Exists(destination) && TestDataGenerator.ComputeSha256(destination) == sourceSha;
    }
    return new BenchmarkResult("aria2", connCount, trial, sizeBytes,
        endToEndSeconds, null,
        sizeBytes / Math.Max(endToEndSeconds, 0.01) / 1048576.0,
        shaOk, null, peakRss / 1048576.0,
        cpuTime.TotalSeconds, process.ExitCode == 0 && shaOk,
        DownloadOnlySeconds: splitTiming ? downloadOnlySeconds : null,
        EndToEndSeconds: splitTiming ? endToEndSeconds : null);
}

void PrintSummary()
{
    Console.WriteLine();
    Console.WriteLine("[bench] summary (median of trials):");
    foreach (var group in results.GroupBy(r => (r.Tool, r.Connections)).OrderBy(g => g.Key.Tool).ThenBy(g => g.Key.Connections))
    {
        var durations = group.Select(r => r.DurationSeconds).OrderBy(d => d).ToArray();
        var median = durations[durations.Length / 2];
        var throughput = group.Key.Connections == 0 ? 0 : group.Select(r => r.ThroughputMBs).OrderBy(t => t).ToArray()[durations.Length / 2];
        var allOk = group.All(r => r.ShaOk && r.Success);
        Console.WriteLine($"[bench]   {group.Key.Tool,-6} conns={group.Key.Connections,2}: median {median,6:F2}s  {throughput,6:F1} MB/s  sha-ok={allOk}  n={group.Count()}");
        if (splitTiming)
        {
            var downloadOnly = group.Select(r => r.DownloadOnlySeconds ?? r.DurationSeconds).OrderBy(d => d).ToArray();
            var downloadOnlyMedian = downloadOnly[downloadOnly.Length / 2];
            var downloadOnlyThroughput = sizeBytes / Math.Max(downloadOnlyMedian, 0.01) / 1048576.0;
            Console.WriteLine($"[bench]   {group.Key.Tool,-6} conns={group.Key.Connections,2}: download-only median {downloadOnlyMedian,6:F2}s  {downloadOnlyThroughput,6:F1} MB/s (end-to-end above)");
        }
    }
}

static int ArgInt(string name, int defaultValue)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name && int.TryParse(args[i + 1], out var value))
            return value;
    return defaultValue;
}

static string ArgString(string name, string defaultValue)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            return args[i + 1];
    return defaultValue;
}

static string? ArgStringOrNull(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            return args[i + 1];
    return null;
}

/// <summary>Counts journal range writes so the report can show DB overhead.</summary>
sealed class CountingJournal : IDownloadJournal
{
    private readonly IDownloadJournal _inner;
    public int Writes;
    public CountingJournal(IDownloadJournal inner) => _inner = inner;
    public Task SaveJobAsync(DownloadJob job, CancellationToken ct = default) => _inner.SaveJobAsync(job, ct);
    public Task<DownloadJob?> LoadJobAsync(Guid id, CancellationToken ct = default) => _inner.LoadJobAsync(id, ct);
    public Task<IReadOnlyList<DownloadJob>> ListJobsAsync(CancellationToken ct = default) => _inner.ListJobsAsync(ct);
    public Task DeleteJobAsync(Guid id, CancellationToken ct = default) => _inner.DeleteJobAsync(id, ct);
    public Task UpdateJobStateAsync(Guid id, DownloadState state, string? error, CancellationToken ct = default)
        => _inner.UpdateJobStateAsync(id, state, error, ct);
    public Task<IReadOnlyList<ByteRange>> LoadCompletedRangesAsync(Guid id, CancellationToken ct = default)
        => _inner.LoadCompletedRangesAsync(id, ct);
    public async Task SaveCompletedRangesAsync(Guid id, IReadOnlyList<ByteRange> completed, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Writes);
        await _inner.SaveCompletedRangesAsync(id, completed, ct);
    }
}

sealed record BenchmarkResult(
    string Tool,
    int Connections,
    int Trial,
    long SizeBytes,
    double DurationSeconds,
    double? ResumeSeconds,
    double ThroughputMBs,
    bool ShaOk,
    int? JournalWrites,
    double PeakWorkingSetMB,
    double CpuSeconds,
    bool Success,
    // Split-timing mode only (null in legacy end-to-end mode).
    // DurationSeconds is always the end-to-end number.
    double? DownloadOnlySeconds = null,
    double? EndToEndSeconds = null);

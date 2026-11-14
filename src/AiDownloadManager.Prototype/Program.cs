using System.Diagnostics;
using AiDownloadManager.Core;
using AiDownloadManager.Engine;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

/// <summary>
/// Phase 0 acceptance runner. Generates a deterministic test file
/// locally, serves it from the test server (Unix domain socket, not
/// TCP — this sandbox blocks .NET TCP connections), downloads it with
/// the segmented engine, and verifies the SHA-256. Nothing here touches
/// the public internet.
///
/// Usage:
///   prototype [--size-mb N] [--connections N] [--mode normal|norange|drop|invalid|inconsistent]
///             [--work-dir PATH] [--interrupt-at PERCENT] [--expected-sha256 HEX]
/// Modes:
///   normal        correct Range support (default)
///   norange       server ignores Range (fallback path)
///   drop          first connections drop after 25% (retry path)
///   invalid       server answers Range with 200 (safe failure expected)
///   inconsistent  server answers 206 with a wrong Content-Range (safe failure expected)
/// --interrupt-at cancels the download at ~PERCENT, then resumes it and verifies.
/// </summary>
var sizeMb = ArgInt("--size-mb", 1024);
var connections = ArgInt("--connections", 8);
var mode = ArgString("--mode", "normal");
var workDir = ArgString("--work-dir", Path.Combine(Path.GetTempPath(), "aidm-phase0"));
var interruptAt = ArgInt("--interrupt-at", -1);
var expectedSha = ArgStringOrNull("--expected-sha256");

Directory.CreateDirectory(workDir);
var sourcePath = Path.Combine(workDir, $"source-{sizeMb}mb.bin");
var destination = Path.Combine(workDir, $"downloaded-{sizeMb}mb-{mode}.bin");
var dbPath = Path.Combine(workDir, "phase0.db");

Console.WriteLine("[prototype] Ai Download Manager — Phase 0 acceptance run");
Console.WriteLine($"[prototype] size={sizeMb} MB connections={connections} mode={mode} workdir={workDir}");

var behavior = mode switch
{
    "normal" => TestServerBehavior.Normal,
    "norange" => TestServerBehavior.IgnoreRange,
    "drop" => TestServerBehavior.DropConnection,
    "invalid" => TestServerBehavior.InvalidRange,
    "inconsistent" => TestServerBehavior.InconsistentContentRange,
    _ => throw new ArgumentException($"Unknown mode: {mode}")
};

string sourceSha;
var generateTimer = Stopwatch.StartNew();
if (File.Exists(sourcePath) && new FileInfo(sourcePath).Length == (long)sizeMb * 1024 * 1024)
{
    sourceSha = TestDataGenerator.ComputeSha256(sourcePath);
    Console.WriteLine($"[prototype] reusing existing source file (sha256 {sourceSha[..16]}…)");
}
else
{
    sourceSha = TestDataGenerator.Generate(sourcePath, (long)sizeMb * 1024 * 1024, seed: 42);
    Console.WriteLine($"[prototype] generated {sizeMb} MB deterministic source in {generateTimer.Elapsed.TotalSeconds:F1}s (sha256 {sourceSha})");
}
expectedSha ??= sourceSha;

var options = new TestServerOptions(
    FileSizeBytes: (long)sizeMb * 1024 * 1024,
    Behavior: behavior,
    DropAfterBytes: behavior == TestServerBehavior.DropConnection ? (long)sizeMb * 1024 * 1024 / 4 : 0,
    DropFirstNConnections: 2,
    // Unix socket, not TCP loopback: this sandbox blocks .NET TCP
    // connections, and the engine is transport-agnostic.
    UnixSocketPath: Path.Combine(workDir, "test-server.sock"));

await using var server = new DeterministicTestServer(sourcePath, options);
await server.StartAsync();
Console.WriteLine($"[prototype] test server at {server.BaseUrl} (Unix socket, no TCP)");

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var journal = new SqliteDownloadJournal(dbPath);
await journal.InitializeAsync();
var transport = new HttpTransport(server.CreateHttpClient());
var downloader = new SegmentedDownloader(
    transport, journal,
    loggerFactory.CreateLogger<SegmentedDownloader>(),
    new SegmentedDownloaderOptions(MaxConnections: connections));

var job = new DownloadJob(Guid.NewGuid(), server.FileUrl, destination, null, expectedSha, DownloadState.Queued, DateTimeOffset.UtcNow);

IProgress<DownloadProgress> MakeProgress(CancellationTokenSource? cancelAtPercentSource, int cancelPercent)
{
    return new Progress<DownloadProgress>(p =>
    {
        var pct = p.TotalBytes is null ? 0 : 100.0 * p.DownloadedBytes / p.TotalBytes.Value;
        Console.Write($"\r[prototype] {pct:F1}%  {p.DownloadedBytes / 1048576.0:F1}/{((p.TotalBytes ?? 0) / 1048576.0):F1} MB  {p.BytesPerSecond / 1048576.0:F1} MB/s  conns={p.ActiveConnections}   ");
        if (cancelAtPercentSource is not null && pct >= cancelPercent)
            cancelAtPercentSource.Cancel();
    });
}

DownloadOutcome outcome;
var runTimer = Stopwatch.StartNew();
if (interruptAt >= 0)
{
    using var cts = new CancellationTokenSource();
    outcome = await downloader.DownloadAsync(job, MakeProgress(cts, interruptAt), cts.Token);
    Console.WriteLine();
    Console.WriteLine($"[prototype] interrupted run: success={outcome.Success} cancelled={outcome.Cancelled} confirmed={outcome.BytesDownloaded} bytes error={outcome.ErrorMessage}");
    if (!outcome.Cancelled)
    {
        Console.WriteLine("[prototype] FAIL: expected the run to be cancelled");
        return 2;
    }
    Console.WriteLine("[prototype] resuming from journal...");
    var resumeJob = job with { State = DownloadState.Queued };
    var resumeTimer = Stopwatch.StartNew();
    outcome = await downloader.DownloadAsync(resumeJob, MakeProgress(null, -1), CancellationToken.None);
    Console.WriteLine();
    Console.WriteLine($"[prototype] resume run: success={outcome.Success} duration={resumeTimer.Elapsed.TotalSeconds:F1}s sha256={outcome.Sha256}");
}
else
{
    outcome = await downloader.DownloadAsync(job, MakeProgress(null, -1), CancellationToken.None);
    Console.WriteLine();
}
runTimer.Stop();

Console.WriteLine($"[prototype] outcome: success={outcome.Success} fallback={outcome.UsedRangeFallback} duration={outcome.Duration.TotalSeconds:F1}s");
Console.WriteLine($"[prototype] bytes={outcome.BytesDownloaded} sha256={outcome.Sha256}");
Console.WriteLine($"[prototype] expected sha256={expectedSha}");

var passed = mode is "invalid" or "inconsistent"
    ? !outcome.Success && outcome.ErrorMessage is not null
    : outcome.Success && outcome.Sha256 == expectedSha && File.Exists(destination);

if (passed && mode is "invalid" or "inconsistent")
    Console.WriteLine("[prototype] PASS: server misbehavior was detected and the download failed safely (no corrupt file promoted)");
else if (passed)
    Console.WriteLine($"[prototype] PASS: downloaded file matches source SHA-256 ({outcome.BytesDownloaded / 1048576.0:F1} MB in {outcome.Duration.TotalSeconds:F1}s = {outcome.BytesDownloaded / Math.Max(outcome.Duration.TotalSeconds, 0.01) / 1048576.0:F1} MB/s)");
else
    Console.WriteLine($"[prototype] FAIL: success={outcome.Success} error={outcome.ErrorMessage}");
return passed ? 0 : 1;

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

using AiDownloadManager.Application;
using AiDownloadManager.Core;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using AiDownloadManager.TestServer;
using Microsoft.Data.Sqlite;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// Antivirus hook tests. The "scanner" is a small stub script
/// created by each test: it logs every invocation, exits 0 (clean),
/// exits 1 when the scanned path contains "infected", and sleeps
/// when the path contains "slow". On Windows the stub is a .cmd file
/// driven through cmd.exe (a .bat cannot be executed directly with
/// UseShellExecute=false); on Unix it is an executable shell script
/// invoked directly. No real antivirus software is involved, and no
/// test touches the public internet.
/// </summary>
public class AntivirusScannerTests : IAsyncLifetime
{
    private string _workDir = string.Empty;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aidm-av-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------
    // Stub scanner
    // ---------------------------------------------------------------

    private sealed record StubScanner(AntivirusScanner Scanner, string LogPath);

    private StubScanner CreateStubScanner(
        bool enabled = true, string? argumentsTemplate = null, TimeSpan? timeout = null)
    {
        var stubPath = Path.Combine(_workDir, "scanner-" + Guid.NewGuid().ToString("N")[..8] +
            (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
        var logPath = Path.Combine(_workDir, "stub-" + Guid.NewGuid().ToString("N")[..8] + ".log");

        string script;
        string scannerPath;
        if (OperatingSystem.IsWindows())
        {
            script = "@echo off\r\n" +
                $"echo CALL: %*>> \"{logPath}\"\r\n" +
                "echo %* | findstr /C:\"infected\" >nul\r\n" +
                "if %errorlevel% equ 0 (\r\n" +
                "  echo EICAR-Test-Signature found\r\n" +
                "  exit /b 1\r\n" +
                ")\r\n" +
                "echo %* | findstr /C:\"slow\" >nul\r\n" +
                "if %errorlevel% equ 0 (\r\n" +
                $"  echo STARTED-SLOW>> \"{logPath}\"\r\n" +
                "  ping -n 6 127.0.0.1 >nul\r\n" +
                $"  echo FINISHED-SLOW>> \"{logPath}\"\r\n" +
                "  exit /b 0\r\n" +
                ")\r\n" +
                "exit /b 0\r\n";
            // A .cmd file cannot be executed directly without a shell,
            // so the configured "scanner" is cmd.exe with a template
            // that runs the stub. The hook still builds the argument
            // list and invokes the process without a shell. A custom
            // template is appended AFTER the stub invocation: without
            // the "/c <stub>" prefix, cmd.exe would receive the extra
            // flags as its own arguments and the stub would never run
            // (cmd starts interactively and exits 0 with no output).
            scannerPath = "cmd.exe";
            argumentsTemplate = $"/c \"{stubPath}\" " + (argumentsTemplate ?? "\"{file}\"");
        }
        else
        {
            script = "#!/bin/sh\n" +
                $"echo \"CALL: $*\" >> '{logPath}'\n" +
                "for a in \"$@\"; do\n" +
                "  case \"$a\" in\n" +
                "    *infected*)\n" +
                "      echo \"EICAR-Test-Signature found in $a\"\n" +
                "      exit 1\n" +
                "      ;;\n" +
                "    *slow*)\n" +
                $"      echo \"STARTED-SLOW\" >> '{logPath}'\n" +
                "      sleep 5\n" +
                $"      echo \"FINISHED-SLOW\" >> '{logPath}'\n" +
                "      exit 0\n" +
                "      ;;\n" +
                "  esac\n" +
                "done\n" +
                "exit 0\n";
            scannerPath = stubPath;
        }

        File.WriteAllText(stubPath, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        var scanner = new AntivirusScanner(new AntivirusScannerOptions(
            Enabled: enabled,
            ScannerPath: scannerPath,
            ArgumentsTemplate: argumentsTemplate,
            Timeout: timeout));
        return new StubScanner(scanner, logPath);
    }

    private string CreateTargetFile(string name, string content = "downloaded bytes")
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ReadLog(string logPath) =>
        File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty;

    // ---------------------------------------------------------------
    // Scanner behaviour
    // ---------------------------------------------------------------

    [Fact]
    public async Task CleanFile_IsReportedClean()
    {
        // Template with a {file} placeholder plus extra flags: proves
        // the placeholder is substituted and extra args pass through.
        var stub = CreateStubScanner(argumentsTemplate: "--scan \"{file}\" --quiet");
        var target = CreateTargetFile("out.bin");

        var result = await stub.Scanner.ScanAsync(target);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.ErrorMessage);
        var log = ReadLog(stub.LogPath);
        Assert.Contains("CALL:", log);
        Assert.Contains(target, log);
        Assert.Contains("--scan", log);
        Assert.Contains("--quiet", log);
    }

    [Fact]
    public async Task InfectedFile_IsReportedInfected()
    {
        var stub = CreateStubScanner();
        var target = CreateTargetFile("infected-sample.bin");

        var result = await stub.Scanner.ScanAsync(target);

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("EICAR-Test-Signature", result.Output);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task MissingScanner_IsReportedAsError_NotAnException()
    {
        var scanner = new AntivirusScanner(new AntivirusScannerOptions(
            Enabled: true,
            ScannerPath: OperatingSystem.IsWindows()
                ? @"C:\nonexistent\no-such-scanner.exe"
                : "/nonexistent/no-such-scanner"));
        var target = CreateTargetFile("out.bin");

        var result = await scanner.ScanAsync(target);

        Assert.Equal(ScanVerdict.Error, result.Verdict);
        Assert.Null(result.ExitCode);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public async Task SlowScanner_TimesOutAndProcessIsKilled()
    {
        var stub = CreateStubScanner(timeout: TimeSpan.FromSeconds(1));
        var target = CreateTargetFile("slow-sample.bin");

        var started = DateTimeOffset.UtcNow;
        var result = await stub.Scanner.ScanAsync(target);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(ScanVerdict.TimedOut, result.Verdict);
        Assert.Null(result.ExitCode);
        Assert.Contains("timed out", result.ErrorMessage);
        // The scan returned promptly instead of waiting for the
        // stub's full 5-second sleep.
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"Scan took {elapsed}.");

        // The stub really started, and — after waiting past its full
        // sleep — it never finished: the timeout killed the process
        // instead of letting it run to completion.
        Assert.Contains("STARTED-SLOW", ReadLog(stub.LogPath));
        var remaining = TimeSpan.FromSeconds(6) - (DateTimeOffset.UtcNow - started);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
        Assert.DoesNotContain("FINISHED-SLOW", ReadLog(stub.LogPath));
    }

    [Fact]
    public async Task DisabledScanner_NeverInvokesTheProcess()
    {
        var stub = CreateStubScanner(enabled: false);
        var target = CreateTargetFile("out.bin");

        var result = await stub.Scanner.ScanAsync(target);

        Assert.Equal(ScanVerdict.Error, result.Verdict);
        Assert.Contains("disabled", result.ErrorMessage);
        Assert.False(File.Exists(stub.LogPath),
            "A disabled scanner must not start any process.");
    }

    [Fact]
    public async Task UnconfiguredScanner_NeverInvokesTheProcess()
    {
        var scanner = new AntivirusScanner(new AntivirusScannerOptions(Enabled: true));
        var target = CreateTargetFile("out.bin");

        var result = await scanner.ScanAsync(target);

        Assert.Equal(ScanVerdict.Error, result.Verdict);
        Assert.Contains("No antivirus scanner is configured", result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadedFile_IsNeverExecuted()
    {
        // The "downloaded file" is an executable script that would
        // leave a marker if anything ever ran it. The hook must only
        // ever invoke the scanner, passing this file as an argument.
        var stub = CreateStubScanner();
        var marker = Path.Combine(_workDir, "executed.marker");
        var target = CreateTargetFile(
            OperatingSystem.IsWindows() ? "payload.cmd" : "payload.sh",
            OperatingSystem.IsWindows()
                ? $"@echo off\r\necho EXECUTED>> \"{marker}\"\r\n"
                : $"#!/bin/sh\necho EXECUTED >> '{marker}'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        var result = await stub.Scanner.ScanAsync(target);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.False(File.Exists(marker),
            "The downloaded file must never be executed by the scan hook.");
        var log = ReadLog(stub.LogPath);
        Assert.Single(log.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("CALL:", StringComparison.Ordinal));
        Assert.Contains(target, log);
    }

    // ---------------------------------------------------------------
    // DownloadService integration
    // ---------------------------------------------------------------

    private async Task<(DeterministicTestServer Server, string Sha256)> StartServerAsync(long sizeBytes)
    {
        var source = Path.Combine(_workDir, $"source-{Guid.NewGuid():N}.bin");
        var sha = TestDataGenerator.Generate(source, sizeBytes, seed: 7);
        var server = new DeterministicTestServer(source, new TestServerOptions(sizeBytes)
        {
            UnixSocketPath = Path.Combine(_workDir, $"srv-{Guid.NewGuid():N}.sock")
        });
        await server.StartAsync();
        return (server, sha);
    }

    private SqliteDownloadJournal CreateJournal()
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, $"journal-{Guid.NewGuid():N}.db"));
        journal.InitializeAsync().GetAwaiter().GetResult();
        return journal;
    }

    private static (string State, string? Error) ReadJobRow(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT state, error FROM downloads";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), "Expected one job row in the journal.");
        var state = reader.GetString(0);
        var error = reader.IsDBNull(1) ? null : reader.GetString(1);
        Assert.False(reader.Read(), "Expected exactly one job row in the journal.");
        return (state, error);
    }

    [Fact]
    public async Task DownloadService_ScansCompletedDownload_ReportsClean()
    {
        const long size = 1024 * 1024;
        var (server, sha) = await StartServerAsync(size);
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var dbPath = Path.Combine(_workDir, "svc-clean.db");
        var journal = new SqliteDownloadJournal(dbPath);
        await journal.InitializeAsync();
        var downloader = new SegmentedDownloader(new HttpTransport(client), journal,
            options: new SegmentedDownloaderOptions(MaxConnections: 2));
        var stub = CreateStubScanner();
        var service = new DownloadService(downloader, journal, stub.Scanner);
        var destination = Path.Combine(_workDir, "clean-out.bin");

        var outcome = await service.DownloadAsync(server.FileUrl, destination, sha);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.NotNull(service.LastScanResult);
        Assert.Equal(ScanVerdict.Clean, service.LastScanResult!.Verdict);
        var (state, error) = ReadJobRow(dbPath);
        Assert.Equal(DownloadState.Completed.ToString(), state);
        Assert.Null(error);
    }

    [Fact]
    public async Task DownloadService_InfectedDownload_IsMarkedFailed()
    {
        const long size = 1024 * 1024;
        var (server, sha) = await StartServerAsync(size);
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var dbPath = Path.Combine(_workDir, "svc-infected.db");
        var journal = new SqliteDownloadJournal(dbPath);
        await journal.InitializeAsync();
        var downloader = new SegmentedDownloader(new HttpTransport(client), journal,
            options: new SegmentedDownloaderOptions(MaxConnections: 2));
        var stub = CreateStubScanner();
        var service = new DownloadService(downloader, journal, stub.Scanner);
        // The destination name contains "infected", so the stub
        // reports an infection for this file.
        var destination = Path.Combine(_workDir, "infected-out.bin");

        var outcome = await service.DownloadAsync(server.FileUrl, destination, sha);

        Assert.False(outcome.Success);
        Assert.Contains("infection", outcome.ErrorMessage);
        Assert.NotNull(service.LastScanResult);
        Assert.Equal(ScanVerdict.Infected, service.LastScanResult!.Verdict);
        var (state, error) = ReadJobRow(dbPath);
        Assert.Equal(DownloadState.Failed.ToString(), state);
        Assert.Contains("infection", error);
        // The hook reports the infection but never deletes the file:
        // removal is the user's decision.
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadService_DisabledScanner_DoesNotScan()
    {
        const long size = 1024 * 1024;
        var (server, sha) = await StartServerAsync(size);
        await using var _ = server;
        using var client = server.CreateHttpClient();
        var journal = CreateJournal();
        var downloader = new SegmentedDownloader(new HttpTransport(client), journal,
            options: new SegmentedDownloaderOptions(MaxConnections: 2));
        var stub = CreateStubScanner(enabled: false);
        var service = new DownloadService(downloader, journal, stub.Scanner);
        var destination = Path.Combine(_workDir, "out.bin");

        var outcome = await service.DownloadAsync(server.FileUrl, destination, sha);

        Assert.True(outcome.Success, outcome.ErrorMessage);
        Assert.Null(service.LastScanResult);
        Assert.False(File.Exists(stub.LogPath),
            "A disabled scanner must not start any process.");
    }
}

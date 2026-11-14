using System.Diagnostics;
using System.Text;

namespace AiDownloadManager.Application;

/// <summary>Outcome of a single antivirus scan of one file.</summary>
public enum ScanVerdict
{
    /// <summary>The scanner exited 0: no threat reported.</summary>
    Clean,
    /// <summary>The scanner exited 1: a threat was reported.</summary>
    Infected,
    /// <summary>
    /// The scan could not be completed (scanner missing, could not
    /// start, exited with an unexpected code, or was cancelled).
    /// An Error is never a clean bill of health and never a crash.
    /// </summary>
    Error,
    /// <summary>The scanner did not finish within the timeout and was killed.</summary>
    TimedOut
}

/// <summary>
/// The result of one scan: the verdict, the scanner's exit code when
/// it ran, the scanner's captured output (truncated), and a
/// human-readable explanation when the verdict is not Clean.
/// </summary>
public sealed record ScanResult(
    ScanVerdict Verdict,
    int? ExitCode,
    string Output,
    string? ErrorMessage)
{
    public bool IsClean => Verdict == ScanVerdict.Clean;
}

/// <summary>
/// User configuration for the antivirus hook. Scanning is
/// <b>off by default</b>: <see cref="Enabled"/> is false and
/// <see cref="ScannerPath"/> is null until the user explicitly
/// enables scanning and configures a scanner executable. No
/// antivirus software is bundled, and no scan ever runs without
/// both settings.
/// </summary>
public sealed record AntivirusScannerOptions(
    bool Enabled = false,
    string? ScannerPath = null,
    /// <summary>
    /// Arguments passed to the scanner. May contain a
    /// <c>{file}</c> placeholder, which is replaced by the file
    /// path as a single argument. When the template has no
    /// placeholder, the file path is appended as the last argument.
    /// When null or empty, the file path is the only argument.
    /// Quoted sections (e.g. <c>"{file}"</c>) stay one argument.
    /// </summary>
    string? ArgumentsTemplate = null,
    TimeSpan? Timeout = null)
{
    /// <summary>How long a scan may run before it is killed. Default 120 seconds.</summary>
    public TimeSpan TimeoutValue => Timeout ?? TimeSpan.FromSeconds(120);
}

/// <summary>
/// Opt-in antivirus hook: runs a user-configured scanner executable
/// against a completed download and reports the result. The hook
/// never executes the downloaded file itself.
///
/// Safety rules:
/// - No shell is ever involved (<see cref="ProcessStartInfo.UseShellExecute"/>
///   is false). Arguments are built with
///   <see cref="ProcessStartInfo.ArgumentList"/>, so the file path is
///   always a single argument however strange it looks; a path such
///   as <c>evil"; rm -rf ~</c> is passed literally, never interpreted.
/// - The scanner's stdout/stderr are captured (and truncated), never
///   shown to a shell.
/// - A scan that exceeds its timeout is killed, including its child
///   processes, and reported as <see cref="ScanVerdict.TimedOut"/>.
/// - Every failure mode (disabled, unconfigured, missing scanner,
///   start failure, unexpected exit code, cancellation, timeout) is
///   reported as a <see cref="ScanResult"/>, never thrown.
/// - Exit-code convention (documented, ClamAV-style): 0 is Clean,
///   1 is Infected, any other non-zero code is an Error.
/// </summary>
public sealed class AntivirusScanner
{
    private const int MaxCapturedOutputChars = 8192;

    private readonly AntivirusScannerOptions _options;

    public AntivirusScanner(AntivirusScannerOptions options)
    {
        _options = options;
    }

    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Scans one file. Returns an Error result without starting any
    /// process when scanning is disabled, no scanner is configured,
    /// the scanner cannot be found, or the file does not exist.
    /// </summary>
    public async Task<ScanResult> ScanAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return Error("Antivirus scanning is disabled.");
        if (string.IsNullOrWhiteSpace(_options.ScannerPath))
            return Error("No antivirus scanner is configured.");
        if (ResolveScannerPath(_options.ScannerPath) is not { } scannerPath)
            return Error($"Antivirus scanner not found: {_options.ScannerPath}");
        if (!File.Exists(filePath))
            return Error($"File to scan was not found: {filePath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = scannerPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArguments(filePath))
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return Error("The antivirus scanner could not be started.");
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            return Error($"The antivirus scanner could not be started: {ex.Message}");
        }

        // Read without the caller's token: the streams only close when
        // the process exits, and we kill the process on timeout or
        // cancellation, so these always complete.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            using var timeout = new CancellationTokenSource(_options.TimeoutValue);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token);
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            try { await process.WaitForExitAsync(); } catch (OperationCanceledException) { }
            if (cancellationToken.IsCancellationRequested)
                return Error("Antivirus scan was cancelled.");
            var partial = await CombineOutputAsync(stdoutTask, stderrTask);
            return new ScanResult(
                ScanVerdict.TimedOut,
                null,
                partial,
                $"Antivirus scan timed out after {_options.TimeoutValue.TotalSeconds:0} s; " +
                "the scanner process was terminated.");
        }

        var output = await CombineOutputAsync(stdoutTask, stderrTask);
        return process.ExitCode switch
        {
            0 => new ScanResult(ScanVerdict.Clean, 0, output, null),
            1 => new ScanResult(ScanVerdict.Infected, 1, output,
                "The antivirus scanner reported an infection in the downloaded file."),
            var code => Error($"The antivirus scanner exited with code {code}.", code, output)
        };
    }

    /// <summary>
    /// Builds the scanner's argument list. The {file} placeholder is
    /// replaced inside its token, so the path is always one argument;
    /// no string is ever concatenated into a command line.
    /// </summary>
    private IReadOnlyList<string> BuildArguments(string filePath)
    {
        var template = _options.ArgumentsTemplate;
        if (string.IsNullOrWhiteSpace(template))
            return new[] { filePath };

        var args = new List<string>();
        var sawPlaceholder = false;
        foreach (var token in SplitTemplate(template))
        {
            if (token.Contains("{file}", StringComparison.Ordinal))
            {
                sawPlaceholder = true;
                args.Add(token.Replace("{file}", filePath, StringComparison.Ordinal));
            }
            else
            {
                args.Add(token);
            }
        }
        if (!sawPlaceholder)
            args.Add(filePath);
        return args;
    }

    /// <summary>Whitespace-splits a template, keeping double-quoted sections whole.</summary>
    private static IEnumerable<string> SplitTemplate(string template)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var ch in template)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (hasToken || current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }
            current.Append(ch);
            hasToken = true;
        }
        if (hasToken || current.Length > 0)
            yield return current.ToString();
    }

    /// <summary>
    /// Resolves the configured scanner: an existing file path is used
    /// as-is; a bare command name is looked up on PATH (with the usual
    /// Windows executable extensions). Returns null when not found.
    /// </summary>
    private static string? ResolveScannerPath(string configured)
    {
        if (File.Exists(configured))
            return configured;
        if (configured.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            return null;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.Combine(dir.Trim(), configured);
            if (File.Exists(candidate))
                return candidate;
            if (OperatingSystem.IsWindows())
            {
                foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
                {
                    var withExt = candidate + ext;
                    if (File.Exists(withExt))
                        return withExt;
                }
            }
        }
        return null;
    }

    private static void KillQuietly(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    private static async Task<string> CombineOutputAsync(
        Task<string> stdoutTask, Task<string> stderrTask)
    {
        var stdout = string.Empty;
        var stderr = string.Empty;
        try { stdout = await stdoutTask; } catch (Exception) { }
        try { stderr = await stderrTask; } catch (Exception) { }
        var combined = stdout;
        if (!string.IsNullOrEmpty(stderr))
            combined += (combined.Length > 0 ? "\n" : string.Empty) + stderr;
        if (combined.Length > MaxCapturedOutputChars)
            combined = combined[..MaxCapturedOutputChars] + "\n…[scanner output truncated]";
        return combined;
    }

    private static ScanResult Error(string message, int? exitCode = null, string output = "") =>
        new(ScanVerdict.Error, exitCode, output, message);
}

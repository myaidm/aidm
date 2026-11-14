using System.Diagnostics;
using System.Reflection;
using Xunit;

/// <summary>
/// Minimal in-process xUnit runner.
///
/// Why this exists: the standard `dotnet test` path needs the vstest
/// testhost, which talks to vstest.console over TCP — and this sandbox
/// blocks .NET TCP connections (the "other_tcp" permission is Deny),
/// so `dotnet test` can never connect here. This runner loads the test
/// assembly and invokes the [Fact] methods directly, in-process, one
/// at a time, with a fresh test-class instance per test (xUnit
/// semantics) and IAsyncLifetime support. It is test infrastructure
/// only and is not shipped.
///
/// Usage: testrunner [--filter SUBSTRING] [--list]
/// Exit code 0 when every executed test passes, 1 otherwise.
/// </summary>
var filter = ArgOrNull("--filter");
var listOnly = HasFlag("--list");

var assembly = Assembly.Load("AiDownloadManager.Engine.Tests");
var testClasses = assembly.GetTypes()
    .Where(t => t.IsClass && !t.IsAbstract && t.GetMethods().Any(m => m.GetCustomAttribute<FactAttribute>() is not null))
    .OrderBy(t => t.FullName)
    .ToList();

var passed = 0;
var failed = 0;
var skipped = 0;
var failures = new List<string>();
var totalTimer = Stopwatch.StartNew();

foreach (var testClass in testClasses)
{
    foreach (var method in testClass.GetMethods()
        .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
        .OrderBy(m => m.Name))
    {
        var name = $"{testClass.Name}.{method.Name}";
        if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            continue;
        if (listOnly)
        {
            Console.WriteLine(name);
            continue;
        }

        var timer = Stopwatch.StartNew();
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(testClass)!;
            if (instance is IAsyncLifetime lifetime)
                await lifetime.InitializeAsync();

            var result = method.Invoke(instance, null);
            if (result is Task task)
                await task;

            Console.WriteLine($"PASS {name} ({timer.Elapsed.TotalSeconds:F1}s)");
            passed++;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException is not null
                ? tie.InnerException
                : ex;
            Console.WriteLine($"FAIL {name} ({timer.Elapsed.TotalSeconds:F1}s): {inner.GetType().Name}: {FirstLine(inner.Message)}");
            failures.Add($"{name}: {FirstLine(inner.Message)}");
            failed++;
        }
        finally
        {
            try
            {
                if (instance is IAsyncLifetime lifetime)
                    await lifetime.DisposeAsync();
                if (instance is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN {name}: cleanup failed: {FirstLine(ex.Message)}");
            }
        }
    }
}

// Report [Theory] methods as skipped (this runner only executes [Fact]).
foreach (var testClass in testClasses)
{
    foreach (var method in testClass.GetMethods())
    {
        if (method.GetCustomAttribute<TheoryAttribute>() is not null)
        {
            Console.WriteLine($"SKIP {testClass.Name}.{method.Name} (Theory not supported by this runner)");
            skipped++;
        }
    }
}

totalTimer.Stop();
Console.WriteLine();
Console.WriteLine($"Tests: {passed} passed, {failed} failed, {skipped} skipped ({totalTimer.Elapsed.TotalSeconds:F1}s)");
if (failures.Count > 0)
{
    Console.WriteLine("Failures:");
    foreach (var failure in failures)
        Console.WriteLine($"  - {failure}");
}
return failed == 0 ? 0 : 1;

static string? ArgOrNull(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            return args[i + 1];
    return null;
}

static bool HasFlag(string name) =>
    Environment.GetCommandLineArgs().Contains(name);

static string FirstLine(string text)
{
    var line = text.Split('\n')[0].Trim();
    return line.Length > 300 ? line[..300] + "…" : line;
}

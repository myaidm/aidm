using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// Static audit of the Presentation XAML: no binding may push values back
/// into a read-only view-model property. A TwoWay (or OneWayToSource)
/// binding on a getter-only property logs a WPF binding error at runtime
/// ("A TwoWay or OneWayToSource binding cannot work on the read-only
/// property ...") and the value never updates from the UI. The trap is
/// that the mode is often implicit: Run.Text and RangeBase.Value
/// (ProgressBar.Value) bind TwoWay by default, so a plain
/// {Binding TotalCount} on a Run is already TwoWay. This test reads the
/// XAML as text (the WPF types cannot load on Linux), so it runs in both
/// the Linux TestRunner and Windows dotnet test.
/// </summary>
public class WpfBindingAuditTests
{
    // Display-only view-model properties actually bound in XAML. Every
    // one is getter-only or has a private setter, so a TwoWay binding to
    // it is a binding error. "FileName" is deliberately absent: it is
    // read-only in the list row but writable in the Add URL dialog, so a
    // single global set cannot judge it (it is bound OneWay in the row
    // and TwoWay to a settable property in the dialog — both correct).
    private static readonly HashSet<string> ReadOnlyDisplayProperties = new(StringComparer.Ordinal)
    {
        "TotalCount", "ActiveDownloadCount", "CurrentSpeedText", "TotalDownloadedText",
        "ProgressPercent",
        "SpeedSamples", "SpeedGraphMax", "CurrentSpeedBytesPerSecond",
        "EmptyStateMessage", "HasEmptyState",
        "StatusText", "ProgressText", "SpeedText", "SizeText", "DownloadedText", "RemainingText",
        "CanEnableAntivirus", "InfoMessage", "ErrorMessage"
    };

    // (Element, attribute) pairs whose dependency property binds TwoWay
    // by default in WPF (FrameworkPropertyMetadataOptions.BindsTwoWayByDefault).
    // A binding on one of these with no explicit Mode is already TwoWay.
    private static readonly HashSet<(string Element, string Attribute)> TwoWayByDefault = new()
    {
        ("Run", "Text"),
        ("ProgressBar", "Value"),
        ("TextBox", "Text"),
        ("ComboBox", "SelectedItem"), ("ComboBox", "Text"),
        ("CheckBox", "IsChecked"),
        ("ListBox", "SelectedItem"), ("ListView", "SelectedItem"), ("Selector", "SelectedItem")
    };

    private static readonly Regex PathRegex = new(@"Path\s*=\s*([^,}\s]+)", RegexOptions.Compiled);
    private static readonly Regex ModeRegex = new(@"Mode\s*=\s*([^,}\s]+)", RegexOptions.Compiled);

    [Fact]
    public void NoTwoWayBindingTargetsReadOnlyDisplayProperty()
    {
        var presentationDir = FindPresentationDirectory();
        var xamlFiles = Directory.GetFiles(presentationDir, "*.xaml", SearchOption.AllDirectories);
        Assert.True(xamlFiles.Length > 0, $"No .xaml files found under {presentationDir}.");

        var problems = new List<string>();
        var audited = 0;
        foreach (var file in xamlFiles)
        {
            var doc = XDocument.Load(file);
            foreach (var element in doc.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var value = attribute.Value;
                    if (!value.Contains("{Binding", StringComparison.Ordinal))
                        continue;
                    audited++;
                    var path = ExtractPath(value);
                    if (string.IsNullOrEmpty(path))
                        continue;
                    var mode = ExtractMode(value);
                    var target = (Element: element.Name.LocalName, Attribute: attribute.Name.LocalName);
                    var effectiveTwoWay = mode is "TwoWay" or "OneWayToSource"
                        || (mode is null && TwoWayByDefault.Contains(target));
                    if (mode == "OneWayToSource")
                        problems.Add($"{Relative(file, presentationDir)}: {target.Element}.{target.Attribute} binds '{path}' with Mode=OneWayToSource, which has no legitimate use in this app.");
                    else if (effectiveTwoWay && ReadOnlyDisplayProperties.Contains(path))
                        problems.Add($"{Relative(file, presentationDir)}: {target.Element}.{target.Attribute} binds read-only '{path}' as TwoWay (explicit Mode={mode ?? "default"}); use Mode=OneWay.");
                }
            }
        }

        Assert.True(audited > 0, "No {Binding} expressions found in the Presentation XAML; the audit ran against nothing.");
        Assert.True(problems.Count == 0,
            "TwoWay bindings on read-only display properties (each logs a WPF binding error at runtime):\n" + string.Join("\n", problems));
    }

    private static string ExtractPath(string binding)
    {
        var pathMatch = PathRegex.Match(binding);
        if (pathMatch.Success)
            return pathMatch.Groups[1].Value;
        // {Binding Property} or {Binding Property, ...}: the first token is the path.
        var after = binding.Substring(binding.IndexOf("{Binding", StringComparison.Ordinal) + "{Binding".Length);
        var token = after.TrimStart().Split(new[] { ',', '}', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token ?? string.Empty;
    }

    private static string? ExtractMode(string binding)
    {
        var modeMatch = ModeRegex.Match(binding);
        return modeMatch.Success ? modeMatch.Groups[1].Value : null;
    }

    private static string FindPresentationDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiDownloadManager.slnx")))
            {
                var presentation = Path.Combine(dir.FullName, "src", "AiDownloadManager.Presentation");
                Assert.True(Directory.Exists(presentation), $"Solution found at {dir.FullName} but {presentation} does not exist.");
                return presentation;
            }
            dir = dir.Parent;
        }
        Assert.Fail($"Could not locate the solution root (AiDownloadManager.slnx) by walking up from {AppContext.BaseDirectory}; the audit cannot run.");
        return string.Empty; // Unreachable: Assert.Fail throws.
    }

    private static string Relative(string file, string root) =>
        Path.GetRelativePath(root, file);
}

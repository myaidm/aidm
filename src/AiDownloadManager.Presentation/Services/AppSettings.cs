using System.IO;
using System.Text.Json;

namespace AiDownloadManager.Presentation.Services;

/// <summary>
/// User settings, stored as JSON in %LOCALAPPDATA%\AiDownloadManager\settings.json.
/// No registry, no cloud, no telemetry. A corrupt or missing file falls
/// back to defaults instead of failing startup.
/// </summary>
public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = string.Empty;
    public int DefaultConnections { get; set; } = 8;

    /// <summary>Optional speed limit in KB/s. Null means unlimited. Stored only for now:
    /// enforcement arrives in a later phase and the UI says so.</summary>
    public int? SpeedLimitKbps { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Reserved for the antivirus hook (Phase 1, separate workstream).
    /// The toggle in Settings is disabled until a scanner path is configured.</summary>
    public bool AntivirusEnabled { get; set; }

    public string AntivirusScannerPath { get; set; } = string.Empty;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as indented JSON.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
                if (settings is not null)
                {
                    settings.DefaultConnections = Math.Clamp(settings.DefaultConnections, 1, 32);
                    if (settings.SpeedLimitKbps is <= 0)
                        settings.SpeedLimitKbps = null;
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt settings must never block startup; defaults apply.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

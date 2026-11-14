using System.IO;
using AiDownloadManager.Presentation.Services;

namespace AiDownloadManager.Presentation.ViewModels;

/// <summary>View-model for the Settings dialog. Edits a copy of the
/// settings; nothing is saved until the user confirms with OK.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private string _downloadFolder;
    private int _defaultConnections;
    private string _speedLimitText;
    private bool _minimizeToTray;
    private bool _antivirusEnabled;
    private string _antivirusScannerPath;
    private string? _errorMessage;

    public SettingsViewModel(AppSettings settings)
    {
        _downloadFolder = settings.DownloadFolder;
        _defaultConnections = settings.DefaultConnections;
        _speedLimitText = settings.SpeedLimitKbps?.ToString() ?? string.Empty;
        _minimizeToTray = settings.MinimizeToTray;
        _antivirusEnabled = settings.AntivirusEnabled;
        _antivirusScannerPath = settings.AntivirusScannerPath;
    }

    public string DownloadFolder
    {
        get => _downloadFolder;
        set => SetProperty(ref _downloadFolder, value);
    }

    public int DefaultConnections
    {
        get => _defaultConnections;
        set => SetProperty(ref _defaultConnections, Math.Clamp(value, 1, 32));
    }

    /// <summary>Empty means unlimited. Stored only — the UI labels it as not yet enforced.</summary>
    public string SpeedLimitText
    {
        get => _speedLimitText;
        set => SetProperty(ref _speedLimitText, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    /// <summary>Only enabled in the UI when a scanner path is configured.</summary>
    public bool AntivirusEnabled
    {
        get => _antivirusEnabled;
        set => SetProperty(ref _antivirusEnabled, value);
    }

    public string AntivirusScannerPath
    {
        get => _antivirusScannerPath;
        set
        {
            if (SetProperty(ref _antivirusScannerPath, value))
            {
                if (string.IsNullOrWhiteSpace(value))
                    AntivirusEnabled = false;
                RaisePropertyChanged(nameof(CanEnableAntivirus));
            }
        }
    }

    public bool CanEnableAntivirus => !string.IsNullOrWhiteSpace(AntivirusScannerPath);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Validates and, on success, writes the edits into <paramref name="target"/>.</summary>
    public bool TryApply(AppSettings target)
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(DownloadFolder))
        {
            ErrorMessage = "Choose a download folder.";
            return false;
        }
        if (!Directory.Exists(DownloadFolder))
        {
            try
            {
                Directory.CreateDirectory(DownloadFolder);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Cannot create the download folder: {ex.Message}";
                return false;
            }
        }
        int? speedLimit = null;
        if (!string.IsNullOrWhiteSpace(SpeedLimitText))
        {
            if (!int.TryParse(SpeedLimitText.Trim(), out var parsed) || parsed <= 0)
            {
                ErrorMessage = "Speed limit must be a positive number of KB/s, or empty for unlimited.";
                return false;
            }
            speedLimit = parsed;
        }
        if (DefaultConnections is < 1 or > 32)
        {
            ErrorMessage = "Default connections must be between 1 and 32.";
            return false;
        }
        if (AntivirusEnabled && string.IsNullOrWhiteSpace(AntivirusScannerPath))
        {
            ErrorMessage = "Choose an antivirus scanner executable before enabling scanning.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(AntivirusScannerPath) && !File.Exists(AntivirusScannerPath))
        {
            ErrorMessage = "The antivirus scanner path does not point to an existing file.";
            return false;
        }

        target.DownloadFolder = DownloadFolder;
        target.DefaultConnections = DefaultConnections;
        target.SpeedLimitKbps = speedLimit;
        target.MinimizeToTray = MinimizeToTray;
        target.AntivirusEnabled = AntivirusEnabled;
        target.AntivirusScannerPath = AntivirusScannerPath;
        return true;
    }
}

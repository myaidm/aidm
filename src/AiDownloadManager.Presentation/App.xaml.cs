using System.IO;
using System.Windows;
using AiDownloadManager.Application;
using AiDownloadManager.Core;
using AiDownloadManager.Engine;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Presentation.Services;
using AiDownloadManager.Presentation.ViewModels;
using AiDownloadManager.Protocols;

namespace AiDownloadManager.Presentation;

/// <summary>
/// Application entry point and composition root: builds the journal,
/// transport, engine, and application service, then shows the main window.
/// The base class is fully qualified on purpose: inside the
/// AiDownloadManager namespace, a bare "Application" resolves to the
/// AiDownloadManager.Application namespace, not System.Windows.Application.
/// </summary>
public partial class App : System.Windows.Application
{
    private SettingsStore? _settingsStore;

    // Distinct unhandled errors already shown to the user this session.
    // A recurring fault (for example a timer tick that throws every
    // second) must not produce a dialog per occurrence: the first
    // occurrence is shown and logged, later identical ones are logged
    // only. A different error still gets its own dialog.
    private readonly HashSet<string> _reportedErrors = new(StringComparer.Ordinal);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiDownloadManager");
            Directory.CreateDirectory(appData);

            _settingsStore = new SettingsStore(Path.Combine(appData, "settings.json"));
            var settings = _settingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.DownloadFolder))
            {
                settings.DownloadFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "AiDownloadManager");
            }
            Directory.CreateDirectory(settings.DownloadFolder);

            var journal = new SqliteDownloadJournal(Path.Combine(appData, "downloads.db"));
            await journal.InitializeAsync();

            var transport = new HttpTransport();
            // One downloader per download (each can use its own
            // connection count); the service builds them from this
            // factory instead of sharing a single instance.
            var downloadService = new DownloadService(journal,
                opts => new SegmentedDownloader(transport, journal, null, opts));

            var historyStore = new DownloadHistoryStore(Path.Combine(appData, "history.json"));
            var viewModel = new MainViewModel(downloadService, settings, historyStore);
            var window = new MainWindow(viewModel, _settingsStore);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex);
            System.Windows.MessageBox.Show(
                $"Ai Download Manager could not start:\n\n{ex.Message}",
                "Ai Download Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // A single failure must never take the whole application down.
        // Binding and configuration faults are recoverable: they are
        // logged in full and shown at most once per distinct error, so
        // a recurring fault cannot spam the user with dialogs.
        var ex = e.Exception;
        LogUnhandledException(ex);
        var key = ex.GetType().FullName + "|" + ex.Message;
        if (_reportedErrors.Add(key))
        {
            System.Windows.MessageBox.Show(
                $"An unexpected error occurred:\n\n{ex.Message}",
                "Ai Download Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        e.Handled = true;
    }

    /// <summary>Appends the full exception (type, message, stack trace) to
    /// %LOCALAPPDATA%\AiDownloadManager\error.log. Best-effort: logging
    /// must never make a bad situation worse, so any failure is swallowed.
    /// A fault the user dismisses is still diagnosable from this file.</summary>
    private static void LogUnhandledException(Exception ex)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiDownloadManager", "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath,
                $"[{DateTimeOffset.UtcNow:O}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}");
        }
        catch
        {
            // Logging is best-effort only.
        }
    }
}

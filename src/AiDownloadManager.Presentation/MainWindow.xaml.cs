using System.Windows;
using AiDownloadManager.Presentation.Services;
using AiDownloadManager.Presentation.ViewModels;
using AiDownloadManager.Presentation.Views;

namespace AiDownloadManager.Presentation;

/// <summary>
/// Main window. Code-behind only handles window mechanics: the tray icon,
/// minimize-to-tray, and opening dialogs. All download logic lives in
/// <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private readonly TrayIconService _tray;
    private bool _isExiting;

    public MainWindow(MainViewModel viewModel, SettingsStore settingsStore)
    {
        _viewModel = viewModel;
        _settingsStore = settingsStore;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.AddUrlRequested += (_, _) => ShowAddUrlDialog();
        _viewModel.SettingsRequested += (_, _) => ShowSettingsDialog();
        _tray = new TrayIconService(ShowWindow, ExitApplication);
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += (_, _) => _viewModel.SaveHistory();
    }

    public void ShowWindow()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _tray.Dispose();
        Close();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting && _viewModel.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            return;
        }
        _tray.Dispose();
    }

    private void ShowAddUrlDialog()
    {
        var addViewModel = new AddUrlViewModel(_viewModel.Settings);
        var dialog = new AddUrlDialog(addViewModel) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        _viewModel.AddDownload(addViewModel.Url, addViewModel.DestinationFolder, addViewModel.FileName, addViewModel.Connections);
    }

    private void ShowSettingsDialog()
    {
        var dialog = new SettingsDialog(_viewModel.Settings, _settingsStore) { Owner = this };
        dialog.ShowDialog();
    }
}

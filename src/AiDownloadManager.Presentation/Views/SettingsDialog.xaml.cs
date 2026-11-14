using System.Windows;
using AiDownloadManager.Presentation.Services;
using AiDownloadManager.Presentation.ViewModels;
using Microsoft.Win32;

namespace AiDownloadManager.Presentation.Views;

/// <summary>
/// Settings dialog. Code-behind only handles window mechanics (dialog
/// result, file/folder pickers); validation and applying live in
/// <see cref="SettingsViewModel"/>.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly SettingsViewModel _viewModel;

    public SettingsDialog(AppSettings settings, SettingsStore store)
    {
        _settings = settings;
        _store = store;
        _viewModel = new SettingsViewModel(settings);
        InitializeComponent();
        DataContext = _viewModel;

        var values = new List<int> { 1, 2, 4, 8, 16, 32 };
        if (!values.Contains(_viewModel.DefaultConnections))
            values.Add(_viewModel.DefaultConnections);
        values.Sort();
        ConnectionsCombo.ItemsSource = values;
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a download folder",
            InitialDirectory = _viewModel.DownloadFolder
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.DownloadFolder = dialog.FolderName;
    }

    private void BrowseScannerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an antivirus scanner executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.AntivirusScannerPath = dialog.FileName;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryApply(_settings))
            return;
        _store.Save(_settings);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

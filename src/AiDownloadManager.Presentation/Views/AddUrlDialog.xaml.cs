using System.Windows;
using AiDownloadManager.Presentation.ViewModels;
using Microsoft.Win32;

namespace AiDownloadManager.Presentation.Views;

/// <summary>
/// Add URL dialog. Code-behind only handles window mechanics (dialog
/// result, folder picker); all validation lives in <see cref="AddUrlViewModel"/>.
/// </summary>
public partial class AddUrlDialog : Window
{
    private readonly AddUrlViewModel _viewModel;

    public AddUrlDialog(AddUrlViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        var values = new List<int> { 1, 2, 4, 8, 16, 32 };
        if (!values.Contains(viewModel.Connections))
            values.Add(viewModel.Connections);
        values.Sort();
        ConnectionsCombo.ItemsSource = values;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a download folder",
            InitialDirectory = _viewModel.DestinationFolder
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.DestinationFolder = dialog.FolderName;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Validate())
            DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

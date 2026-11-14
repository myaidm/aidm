using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using AiDownloadManager.Application;
using AiDownloadManager.Core;
using AiDownloadManager.Presentation.Services;

namespace AiDownloadManager.Presentation.ViewModels;

/// <summary>
/// Main window view-model: the download list, navigation filters,
/// aggregate statistics, and the speed-graph samples. Downloads run
/// through the <see cref="DownloadService"/> composed in App; item
/// view-models receive the service and never touch the engine or the
/// journal directly.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    public static readonly IReadOnlyList<string> NavItems = new[]
    {
        "All", "Downloading", "Completed", "Failed", "Queues", "Categories", "History"
    };

    private const int MaxGraphSamples = 60;

    private readonly DownloadHistoryStore _historyStore;
    private readonly Queue<double> _speedSamples = new();
    private readonly DispatcherTimer _timer;

    private string _selectedNav = "All";
    private DownloadItemViewModel? _selectedItem;
    private string? _emptyStateMessage;
    private bool _hasEmptyState = true;
    private double _currentSpeedBytesPerSecond;
    private double _speedGraphMax = 1;

    public MainViewModel(
        DownloadService downloadService,
        AppSettings settings,
        DownloadHistoryStore historyStore)
    {
        DownloadService = downloadService;
        Settings = settings;
        _historyStore = historyStore;

        Items = new ObservableCollection<DownloadItemViewModel>();
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = FilterItem;

        AddUrlCommand = new RelayCommand(_ => AddUrlRequested?.Invoke(this, EventArgs.Empty));
        StartSelectedCommand = new RelayCommand(async _ => { if (SelectedItem is not null) await SelectedItem.StartAsync(); }, _ => SelectedItem?.CanStart == true);
        PauseSelectedCommand = new RelayCommand(_ => SelectedItem?.Pause(), _ => SelectedItem?.CanPause == true);
        ResumeSelectedCommand = new RelayCommand(async _ => { if (SelectedItem is not null) await SelectedItem.ResumeAsync(); }, _ => SelectedItem?.CanResume == true);
        CancelSelectedCommand = new RelayCommand(async _ => await CancelSelectedAsync(), _ => SelectedItem?.CanCancel == true);
        RemoveSelectedCommand = new RelayCommand(async _ => await RemoveSelectedAsync(), _ => SelectedItem?.CanRemove == true);
        ClearCompletedCommand = new RelayCommand(_ => ClearCompleted(), _ => Items.Any(i => i.State == DownloadState.Completed));
        OpenSettingsCommand = new RelayCommand(_ => SettingsRequested?.Invoke(this, EventArgs.Empty));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();

        LoadHistory();
        RefreshEmptyState();
    }

    /// <summary>Raised when the user clicks Add URL (the window shows the dialog).</summary>
    public event EventHandler? AddUrlRequested;

    /// <summary>Raised when the user clicks Settings (the window shows the dialog).</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>The application service composed in App. Item
    /// view-models drive their downloads through it (start returns a
    /// job handle; pause/resume/cancel address the job by id).</summary>
    public DownloadService DownloadService { get; }

    public AppSettings Settings { get; }

    public ObservableCollection<DownloadItemViewModel> Items { get; }
    public ICollectionView FilteredItems { get; }

    public IReadOnlyList<string> NavigationItems => NavItems;

    public string SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (SetProperty(ref _selectedNav, value))
            {
                FilteredItems.Refresh();
                RefreshEmptyState();
            }
        }
    }

    public DownloadItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public string? EmptyStateMessage
    {
        get => _emptyStateMessage;
        private set => SetProperty(ref _emptyStateMessage, value);
    }

    public bool HasEmptyState
    {
        get => _hasEmptyState;
        private set => SetProperty(ref _hasEmptyState, value);
    }

    public double CurrentSpeedBytesPerSecond
    {
        get => _currentSpeedBytesPerSecond;
        private set
        {
            if (SetProperty(ref _currentSpeedBytesPerSecond, value))
                RaisePropertyChanged(nameof(CurrentSpeedText));
        }
    }

    public string CurrentSpeedText => FormatSpeed(CurrentSpeedBytesPerSecond);
    public int ActiveDownloadCount => Items.Count(i => i.State == DownloadState.Downloading);
    public int TotalCount => Items.Count;

    public string TotalDownloadedText => FormatBytes(Items.Sum(i => i.DownloadedBytes));

    /// <summary>Aggregate throughput samples (bytes/s), oldest first, for the speed graph.</summary>
    public IReadOnlyList<double> SpeedSamples => _speedSamples.ToArray();

    public double SpeedGraphMax
    {
        get => _speedGraphMax;
        private set => SetProperty(ref _speedGraphMax, value);
    }

    public RelayCommand AddUrlCommand { get; }
    public RelayCommand StartSelectedCommand { get; }
    public RelayCommand PauseSelectedCommand { get; }
    public RelayCommand ResumeSelectedCommand { get; }
    public RelayCommand CancelSelectedCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }

    private bool FilterItem(object item)
    {
        if (item is not DownloadItemViewModel vm)
            return false;
        return _selectedNav switch
        {
            "All" => true,
            "Downloading" => vm.State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Paused,
            "Completed" => vm.State == DownloadState.Completed,
            "Failed" => vm.State is DownloadState.Failed or DownloadState.Cancelled,
            // Queues and Categories have no backing feature in Phase 1.
            "Queues" => false,
            "Categories" => false,
            "History" => true,
            _ => true
        };
    }

    private void RefreshEmptyState()
    {
        if (_selectedNav == "Queues")
        {
            EmptyStateMessage = "Queues are not yet implemented in Phase 1.\nDownloads start immediately when added.";
            HasEmptyState = true;
            return;
        }
        if (_selectedNav == "Categories")
        {
            EmptyStateMessage = "Categories are not yet implemented in Phase 1.\nAutomatic categorization arrives in a later phase.";
            HasEmptyState = true;
            return;
        }
        if (FilteredItems.IsEmpty)
        {
            EmptyStateMessage = _selectedNav switch
            {
                "All" => "No downloads yet.\nClick “Add URL” to start your first download.",
                "Downloading" => "Nothing is downloading right now.",
                "Completed" => "No completed downloads yet.",
                "Failed" => "No failed downloads. Failed downloads appear here with the error message.",
                "History" => "No download history yet.",
                _ => "Nothing here yet."
            };
            HasEmptyState = true;
            return;
        }
        EmptyStateMessage = null;
        HasEmptyState = false;
    }

    private void OnTimerTick()
    {
        foreach (var item in Items)
            item.Tick();

        CurrentSpeedBytesPerSecond = Items
            .Where(i => i.State == DownloadState.Downloading)
            .Sum(i => i.SpeedBytesPerSecond);

        _speedSamples.Enqueue(CurrentSpeedBytesPerSecond);
        while (_speedSamples.Count > MaxGraphSamples)
            _speedSamples.Dequeue();
        SpeedGraphMax = Math.Max(1, _speedSamples.Max() * 1.1);
        RaisePropertyChanged(nameof(SpeedSamples));
        RaisePropertyChanged(nameof(ActiveDownloadCount));
        RaisePropertyChanged(nameof(TotalDownloadedText));
        RaisePropertyChanged(nameof(TotalCount));
    }

    private void OnItemStateChanged(object? sender, EventArgs e)
    {
        FilteredItems.Refresh();
        RefreshEmptyState();
        RaisePropertyChanged(nameof(ActiveDownloadCount));
        RaisePropertyChanged(nameof(TotalDownloadedText));
        RaisePropertyChanged(nameof(TotalCount));
        SaveHistory();
    }

    private async void OnItemRemoveRequested(object? sender, DownloadItemViewModel item) =>
        await RemoveItemAsync(item);

    /// <summary>Adds a download and starts it immediately. Returns the created item.</summary>
    public DownloadItemViewModel AddDownload(string url, string folder, string fileName, int connections)
    {
        Directory.CreateDirectory(folder);
        var destination = GetUniqueDestination(folder, fileName);
        var job = new DownloadJob(
            Guid.NewGuid(), url, destination, null, null,
            DownloadState.Queued, DateTimeOffset.UtcNow);
        var item = new DownloadItemViewModel(job, DownloadService, connections);
        item.StateChanged += OnItemStateChanged;
        item.RemoveRequested += OnItemRemoveRequested;
        Items.Add(item);
        SelectedItem = item;
        SaveHistory();
        RefreshEmptyState();
        _ = item.StartAsync();
        return item;
    }

    public async Task CancelSelectedAsync()
    {
        if (SelectedItem is null)
            return;
        await SelectedItem.CancelAsync();
    }

    public async Task RemoveSelectedAsync()
    {
        if (SelectedItem is null)
            return;
        await RemoveItemAsync(SelectedItem);
    }

    public async Task RemoveItemAsync(DownloadItemViewModel item)
    {
        if (item.State == DownloadState.Downloading)
        {
            await item.CancelAsync();
            await item.CompletionTask;
        }

        // A completed file belongs to the user: ask before deleting it.
        // A partial (.part) file is deleted silently — it is not usable.
        if (item.State == DownloadState.Completed && File.Exists(item.DestinationPath))
        {
            var answer = System.Windows.MessageBox.Show(
                $"Also delete the downloaded file?\n\n{item.DestinationPath}",
                "Remove download",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);
            if (answer == System.Windows.MessageBoxResult.Cancel)
                return;
            if (answer == System.Windows.MessageBoxResult.Yes)
            {
                try { File.Delete(item.DestinationPath); } catch { /* best effort */ }
            }
        }
        else
        {
            try
            {
                var part = item.DestinationPath + ".part";
                if (File.Exists(part))
                    File.Delete(part);
            }
            catch { /* best effort */ }
        }

        item.StateChanged -= OnItemStateChanged;
        item.RemoveRequested -= OnItemRemoveRequested;
        Items.Remove(item);
        if (SelectedItem == item)
            SelectedItem = null;
        SaveHistory();
        RefreshEmptyState();
    }

    private void ClearCompleted()
    {
        var completed = Items.Where(i => i.State == DownloadState.Completed).ToList();
        foreach (var item in completed)
        {
            item.StateChanged -= OnItemStateChanged;
            item.RemoveRequested -= OnItemRemoveRequested;
            Items.Remove(item);
        }
        if (SelectedItem is not null && !Items.Contains(SelectedItem))
            SelectedItem = null;
        SaveHistory();
        RefreshEmptyState();
    }

    private static string GetUniqueDestination(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate) && !File.Exists(candidate + ".part"))
            return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 1; ; n++)
        {
            candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".part"))
                return candidate;
        }
    }

    private void LoadHistory()
    {
        foreach (var record in _historyStore.Load())
        {
            if (!Enum.TryParse<DownloadState>(record.State, out var state))
                continue;
            // A download that was running when the app closed was
            // interrupted: it can be resumed, so show it as Paused.
            if (state is DownloadState.Downloading or DownloadState.Queued)
                state = DownloadState.Paused;
            var job = new DownloadJob(
                record.Id, record.Url, record.DestinationPath, record.TotalBytes,
                null, state, record.CreatedAt);
            var item = new DownloadItemViewModel(job, DownloadService, record.Connections);
            item.Restore(record.DownloadedBytes, record.ErrorMessage);
            item.StateChanged += OnItemStateChanged;
            item.RemoveRequested += OnItemRemoveRequested;
            Items.Add(item);
        }
    }

    public void SaveHistory()
    {
        var records = Items.Select(i => new DownloadHistoryRecord
        {
            Id = i.Id,
            Url = i.Url,
            DestinationPath = i.DestinationPath,
            TotalBytes = i.TotalBytes,
            DownloadedBytes = i.DownloadedBytes,
            State = i.State.ToString(),
            ErrorMessage = i.ErrorMessage,
            Connections = i.Connections,
            CreatedAt = i.Job.CreatedAt
        }).ToList();
        _historyStore.Save(records);
    }
}

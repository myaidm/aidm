using System.IO;
using AiDownloadManager.Application;
using AiDownloadManager.Core;
using AiDownloadManager.Engine;

namespace AiDownloadManager.Presentation.ViewModels;

/// <summary>
/// One row in the download list. Drives downloads through
/// <see cref="DownloadService"/>: starting returns a
/// <see cref="DownloadHandle"/> (job id + completion task), and
/// pause / resume / cancel address the job by id through the service.
/// This class never touches the engine or the journal directly — the
/// service owns the download lifecycle and the journal stays the
/// source of truth for resume. This class only mirrors that state
/// for display and maps user actions onto service calls.
/// </summary>
public sealed class DownloadItemViewModel : ViewModelBase
{
    private readonly DownloadService _service;

    private Task? _runningTask;
    private bool _cancelRequested;
    private long _lastReportedBytes;
    private DateTime _lastProgressAt = DateTime.UtcNow;

    private DownloadState _state;
    private long _downloadedBytes;
    private long? _totalBytes;
    private double _speedBytesPerSecond;
    private string? _errorMessage;

    public DownloadItemViewModel(
        DownloadJob job, DownloadService service, int connections)
    {
        Job = job;
        _service = service;
        Connections = Math.Clamp(connections, 1, 32);
        _state = job.State;
        _totalBytes = job.TotalBytes;
        FileName = Path.GetFileName(job.DestinationPath);

        StartCommand = new RelayCommand(async _ => await StartAsync(), _ => CanStart);
        PauseCommand = new RelayCommand(_ => Pause(), _ => CanPause);
        ResumeCommand = new RelayCommand(async _ => await ResumeAsync(), _ => CanResume);
        CancelCommand = new RelayCommand(async _ => await CancelAsync(), _ => CanCancel);
        RemoveCommand = new RelayCommand(_ => RemoveRequested?.Invoke(this, this), _ => CanRemove);
    }

    public DownloadJob Job { get; }
    public Guid Id => Job.Id;
    public string Url => Job.Url;
    public string DestinationPath => Job.DestinationPath;
    public string FileName { get; }
    public int Connections { get; }

    /// <summary>Raised when the state changes (not on every progress report).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when the user asks to remove this item from the list.</summary>
    public event EventHandler<DownloadItemViewModel>? RemoveRequested;

    public DownloadState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                RaisePropertyChanged(nameof(StatusText));
                RaisePropertyChanged(nameof(CanStart));
                RaisePropertyChanged(nameof(CanPause));
                RaisePropertyChanged(nameof(CanResume));
                RaisePropertyChanged(nameof(CanCancel));
                RaisePropertyChanged(nameof(CanRemove));
                RaisePropertyChanged(nameof(IsActive));
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string StatusText => State.ToString();

    public bool IsActive => State == DownloadState.Downloading;

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        private set
        {
            if (SetProperty(ref _downloadedBytes, value))
            {
                RaisePropertyChanged(nameof(DownloadedText));
                RaisePropertyChanged(nameof(ProgressPercent));
                RaisePropertyChanged(nameof(ProgressText));
                RaisePropertyChanged(nameof(RemainingText));
            }
        }
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        private set
        {
            if (SetProperty(ref _totalBytes, value))
            {
                RaisePropertyChanged(nameof(SizeText));
                RaisePropertyChanged(nameof(ProgressPercent));
                RaisePropertyChanged(nameof(ProgressText));
                RaisePropertyChanged(nameof(RemainingText));
            }
        }
    }

    public double ProgressPercent =>
        TotalBytes is null or <= 0 ? 0
        : Math.Clamp(100.0 * DownloadedBytes / TotalBytes.Value, 0, 100);

    public string ProgressText => TotalBytes is null ? $"{FormatBytes(DownloadedBytes)}" : $"{ProgressPercent:F0}%";

    public double SpeedBytesPerSecond
    {
        get => _speedBytesPerSecond;
        private set
        {
            if (SetProperty(ref _speedBytesPerSecond, value))
                RaisePropertyChanged(nameof(SpeedText));
        }
    }

    public string SpeedText => IsActive ? FormatSpeed(SpeedBytesPerSecond) : "—";
    public string SizeText => TotalBytes is null ? "Unknown" : FormatBytes(TotalBytes.Value);
    public string DownloadedText => FormatBytes(DownloadedBytes);

    public string RemainingText
    {
        get
        {
            if (!IsActive || TotalBytes is null || SpeedBytesPerSecond <= 0)
                return "—";
            var remainingBytes = TotalBytes.Value - DownloadedBytes;
            if (remainingBytes <= 0)
                return "—";
            return FormatRemaining(TimeSpan.FromSeconds(remainingBytes / SpeedBytesPerSecond));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool CanStart => State == DownloadState.Queued;
    public bool CanPause => State == DownloadState.Downloading;
    public bool CanResume => State is DownloadState.Paused or DownloadState.Failed;
    public bool CanCancel => State is DownloadState.Downloading or DownloadState.Paused or DownloadState.Failed or DownloadState.Queued;
    public bool CanRemove => State != DownloadState.Downloading;

    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RemoveCommand { get; }

    /// <summary>Restores display state for an item loaded from history (journal is the truth for resume).</summary>
    public void Restore(long downloadedBytes, string? errorMessage)
    {
        _downloadedBytes = downloadedBytes;
        _errorMessage = errorMessage;
        RaisePropertyChanged(nameof(DownloadedBytes));
        RaisePropertyChanged(nameof(DownloadedText));
        RaisePropertyChanged(nameof(ProgressPercent));
        RaisePropertyChanged(nameof(ProgressText));
        RaisePropertyChanged(nameof(ErrorMessage));
    }

    /// <summary>Called once a second by the main view-model: a stalled download shows 0 speed, not a stale rate.</summary>
    public void Tick()
    {
        if (IsActive && (DateTime.UtcNow - _lastProgressAt) > TimeSpan.FromSeconds(2.5))
            SpeedBytesPerSecond = 0;
    }

    /// <summary>A task that completes when the currently running download finishes (pause/cancel/complete/fail).</summary>
    public Task CompletionTask => _runningTask ?? Task.CompletedTask;

    public Task StartAsync()
    {
        if (!CanStart && State != DownloadState.Queued)
            return Task.CompletedTask;
        _runningTask = RunAsync();
        return _runningTask;
    }

    public Task ResumeAsync()
    {
        if (!CanResume)
            return Task.CompletedTask;
        _runningTask = RunAsync();
        return _runningTask;
    }

    public void Pause()
    {
        if (State != DownloadState.Downloading)
            return;
        // The service cancels the download's token; the engine reports
        // the cancellation as an outcome and ApplyOutcome maps it to
        // Paused (partial file and journal ranges are kept) unless
        // Cancel was requested instead.
        _ = _service.PauseAsync(Job.Id);
    }

    public async Task CancelAsync()
    {
        // The service stops the engine, waits for it to finish, and
        // only then discards the partial download (journal ranges +
        // .part file), so the discard cannot race the engine's final
        // persist. A non-running job is discarded directly.
        _cancelRequested = true;
        await _service.CancelAsync(Job.Id);
        ErrorMessage = null;
        State = DownloadState.Cancelled;
    }

    private async Task RunAsync()
    {
        if (State == DownloadState.Downloading || State == DownloadState.Completed)
            return;

        // FTP has no transport selected in this build (the FTP
        // transport exists since Phase 1, but the app does not select
        // a transport by URL scheme yet). Fail fast with a clear
        // message instead of letting HttpClient throw a confusing error.
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeFtp)
        {
            ErrorMessage = "FTP downloads are not yet supported in this build. The FTP transport exists, but the app does not select a transport by URL scheme yet.";
            State = DownloadState.Failed;
            return;
        }

        _cancelRequested = false;
        ErrorMessage = null;
        _lastReportedBytes = DownloadedBytes;
        _lastProgressAt = DateTime.UtcNow;
        State = DownloadState.Downloading;

        var progress = new Progress<DownloadProgress>(OnProgress);
        DownloadOutcome outcome;
        try
        {
            // The service starts the download as tracked work and
            // returns a handle; awaiting its Completion yields the
            // outcome (the same call serves both Start and Resume —
            // the engine reads the journal ranges either way).
            var handle = await _service.StartDownloadAsync(Job, progress, Connections);
            outcome = await handle.Completion;
        }
        catch (Exception ex)
        {
            // The engine converts failures into an outcome; this is only a
            // safety net so a download can never crash the application.
            ErrorMessage = ex.Message;
            State = DownloadState.Failed;
            return;
        }

        ApplyOutcome(outcome);
    }

    private void OnProgress(DownloadProgress p)
    {
        // Progress<T> was created on the UI thread, so this runs there.
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastProgressAt).TotalSeconds;
        if (elapsed >= 0.25 && p.DownloadedBytes >= _lastReportedBytes)
        {
            var instant = (p.DownloadedBytes - _lastReportedBytes) / elapsed;
            // Light smoothing so the displayed rate does not jump wildly.
            SpeedBytesPerSecond = SpeedBytesPerSecond <= 0
                ? instant
                : SpeedBytesPerSecond * 0.6 + instant * 0.4;
            _lastReportedBytes = p.DownloadedBytes;
            _lastProgressAt = now;
        }
        DownloadedBytes = p.DownloadedBytes;
        if (p.TotalBytes is not null)
            TotalBytes = p.TotalBytes;
    }

    private void ApplyOutcome(DownloadOutcome outcome)
    {
        // The outcome's byte count is journal-confirmed; trust it over the
        // last progress report.
        DownloadedBytes = outcome.BytesDownloaded;
        SpeedBytesPerSecond = 0;

        if (outcome.Success)
        {
            if (TotalBytes is not null)
                DownloadedBytes = TotalBytes.Value;
            ErrorMessage = null;
            State = DownloadState.Completed;
            return;
        }

        if (outcome.Cancelled)
        {
            if (_cancelRequested)
            {
                // The service already discarded the partial download
                // (journal ranges + .part file) when Cancel was called.
                ErrorMessage = null;
                State = DownloadState.Cancelled;
            }
            else
            {
                // Pause (or an external cancel): keep the partial file and
                // the journal ranges so Resume continues where it stopped.
                ErrorMessage = null;
                State = DownloadState.Paused;
            }
            return;
        }

        ErrorMessage = outcome.ErrorMessage ?? "Download failed.";
        State = DownloadState.Failed;
    }
}

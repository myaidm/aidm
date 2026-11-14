using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiDownloadManager.Presentation.ViewModels;

/// <summary>Base class for view-models: property-change notification only.</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    /// <summary>Human-readable byte counts and transfer rates. No external dependency.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }

    public static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "—" : $"{FormatBytes((long)bytesPerSecond)}/s";

    public static string FormatRemaining(TimeSpan? remaining) =>
        remaining is null ? "—"
        : remaining.Value.TotalHours >= 1 ? remaining.Value.ToString(@"h\:mm\:ss")
        : remaining.Value.ToString(@"m\:ss");
}

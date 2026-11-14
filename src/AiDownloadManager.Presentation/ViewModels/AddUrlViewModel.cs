using System.IO;
using AiDownloadManager.Presentation.Services;

namespace AiDownloadManager.Presentation.ViewModels;

/// <summary>View-model for the Add URL dialog. Validation lives here; the
/// dialog window only maps buttons to DialogResult.</summary>
public sealed class AddUrlViewModel : ViewModelBase
{
    private string _url = string.Empty;
    private string _destinationFolder;
    private string _fileName = string.Empty;
    private int _connections;
    private string? _errorMessage;
    private string? _infoMessage;
    private bool _fileNameAutoDerived = true;

    public AddUrlViewModel(AppSettings settings)
    {
        _destinationFolder = settings.DownloadFolder;
        _connections = Math.Clamp(settings.DefaultConnections, 1, 32);
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value))
                OnUrlChanged();
        }
    }

    public string DestinationFolder
    {
        get => _destinationFolder;
        set => SetProperty(ref _destinationFolder, value);
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (SetProperty(ref _fileName, value))
                _fileNameAutoDerived = false;
        }
    }

    public int Connections
    {
        get => _connections;
        set => SetProperty(ref _connections, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string? InfoMessage
    {
        get => _infoMessage;
        private set => SetProperty(ref _infoMessage, value);
    }

    private void OnUrlChanged()
    {
        InfoMessage = null;
        if (Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeFtp)
                InfoMessage = "FTP URLs are accepted, but FTP downloads are not yet supported in this build — the FTP transport is being added in Phase 1.";
            if (_fileNameAutoDerived)
            {
                var derived = DeriveFileName(uri);
                _fileName = derived;
                RaisePropertyChanged(nameof(FileName));
            }
        }
    }

    /// <summary>Validates all fields; sets <see cref="ErrorMessage"/> and returns false on the first problem.</summary>
    public bool Validate()
    {
        ErrorMessage = null;
        var url = Url.Trim();
        if (url.Length == 0)
        {
            ErrorMessage = "Enter a download URL.";
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ErrorMessage = "The URL is not a valid absolute URL (for example: https://example.com/file.zip).";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp)
        {
            ErrorMessage = $"Unsupported URL scheme “{uri.Scheme}”. Use http, https, or ftp.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(DestinationFolder))
        {
            ErrorMessage = "Choose a destination folder.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(FileName))
        {
            ErrorMessage = "Enter a file name.";
            return false;
        }
        var invalid = Path.GetInvalidFileNameChars();
        if (FileName.IndexOfAny(invalid) >= 0)
        {
            ErrorMessage = "The file name contains characters that are not allowed in Windows file names.";
            return false;
        }
        if (Connections is < 1 or > 32)
        {
            ErrorMessage = "Connections must be between 1 and 32.";
            return false;
        }
        Url = url;
        return true;
    }

    public static string DeriveFileName(Uri uri)
    {
        var lastSegment = uri.Segments.Length == 0 ? string.Empty : uri.Segments[^1].Trim('/');
        var decoded = Uri.UnescapeDataString(lastSegment);
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(decoded.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned))
            return "download";
        return cleaned;
    }
}

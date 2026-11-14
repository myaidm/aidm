using System.IO;
using System.Text.Json;

namespace AiDownloadManager.Presentation.Services;

/// <summary>
/// One persisted download-list entry. This is the presentation layer's
/// own history store (JSON in %LOCALAPPDATA%\AiDownloadManager\history.json).
/// It exists because <see cref="Core.IDownloadJournal"/> has no list
/// operation: the engine journal remains the source of truth for byte
/// ranges (resume), while this file only remembers what to show in the
/// list and in History after a restart. It never stores passwords,
/// tokens, cookies, or headers — only the URL, destination, and progress.
/// </summary>
public sealed class DownloadHistoryRecord
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long? TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public string State { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public int Connections { get; set; } = 8;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Loads and saves the download history as indented JSON.</summary>
public sealed class DownloadHistoryStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DownloadHistoryStore(string path) => _path = path;

    public IReadOnlyList<DownloadHistoryRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var records = JsonSerializer.Deserialize<List<DownloadHistoryRecord>>(File.ReadAllText(_path));
                if (records is not null)
                    return records;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt history file must never block startup.
        }
        return Array.Empty<DownloadHistoryRecord>();
    }

    public void Save(IReadOnlyList<DownloadHistoryRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(records, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History persistence is best-effort; a failure here must not
            // interrupt a download.
        }
    }
}

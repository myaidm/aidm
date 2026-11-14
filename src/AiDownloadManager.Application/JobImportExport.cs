using System.Globalization;
using System.Text;
using System.Text.Json;
using AiDownloadManager.Core;

namespace AiDownloadManager.Application;

/// <summary>
/// Result of an import attempt. A batch is all-or-nothing: if
/// <see cref="Errors"/> is non-empty, <see cref="Imported"/> is empty
/// and nothing was persisted, even when some records in the file were
/// individually valid. Duplicates are not errors: they are skipped and
/// listed in <see cref="SkippedDuplicates"/>.
/// </summary>
public sealed record ImportResult(
    IReadOnlyList<DownloadJob> Imported,
    IReadOnlyList<DownloadJob> SkippedDuplicates,
    IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Import and export of download jobs (and, by extension, download
/// history: history is the journal's job list) in JSON and CSV.
///
/// Security model:
/// - The exporter writes ONLY the six whitelisted fields below. The
///   job model carries no passwords, tokens, cookies, or credentials
///   of any kind, and the exporter never writes anything else, so an
///   exported file cannot leak a secret even if one were added to a
///   job record later: new fields are not exported unless explicitly
///   added to <see cref="ExportedFieldNames"/>.
/// - Imported data is untrusted. Every record is validated before
///   anything is persisted: URLs must be absolute http/https/ftp
///   without embedded credentials; destinations must be well-formed
///   paths (relative paths containing ".." are rejected); total bytes
///   must be non-negative; SHA-256 must be 64 hex characters.
/// - CSV output uses RFC 4180 quoting, so a field that looks like a
///   spreadsheet formula (for example "=cmd|...") is stored as inert
///   quoted text. This code never evaluates, executes, or interprets
///   imported field values in any way.
/// </summary>
public static class JobImportExport
{
    /// <summary>
    /// The only fields ever written by the exporter, in export order.
    /// Nothing else leaves the application through import/export.
    /// </summary>
    public static readonly string[] ExportedFieldNames =
        { "url", "destinationPath", "totalBytes", "expectedSha256", "state", "createdAt" };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ------------------------------------------------------------------
    // Export
    // ------------------------------------------------------------------

    /// <summary>Serializes jobs to JSON. Only the whitelisted fields are written.</summary>
    public static string ExportJobsToJson(IEnumerable<DownloadJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var dtos = jobs.Select(ToDto).ToList();
        return JsonSerializer.Serialize(dtos, JsonWriteOptions);
    }

    /// <summary>Serializes jobs to RFC 4180 CSV. Only the whitelisted fields are written.</summary>
    public static string ExportJobsToCsv(IEnumerable<DownloadJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", ExportedFieldNames));
        foreach (var job in jobs)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                QuoteCsv(job.Url),
                QuoteCsv(job.DestinationPath),
                QuoteCsv(job.TotalBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                QuoteCsv(job.ExpectedSha256 ?? string.Empty),
                QuoteCsv(job.State.ToString()),
                QuoteCsv(job.CreatedAt.ToString("O", CultureInfo.InvariantCulture))
            }));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Import: parse + validate (pure, persists nothing)
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses and validates a JSON export (an array of job objects).
    /// Returns the validated jobs and any duplicates skipped. If any
    /// record is invalid, <see cref="ImportResult.Success"/> is false
    /// and <see cref="ImportResult.Imported"/> is empty: the caller
    /// must persist nothing from a failed batch.
    /// </summary>
    public static ImportResult ParseJobsFromJson(
        string? json, IReadOnlyCollection<DownloadJob>? existingJobs = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Failed("The file is empty.");

        List<ImportJobDto> dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<ImportJobDto>>(json, JsonReadOptions)
                ?? throw new JsonException("The JSON document is empty.");
        }
        catch (JsonException ex)
        {
            return Failed($"Invalid JSON: {ex.Message}");
        }

        return ValidateDtos(dtos, existingJobs);
    }

    /// <summary>
    /// Parses and validates a CSV export (header row + one job per row).
    /// Same all-or-nothing contract as <see cref="ParseJobsFromJson"/>.
    /// </summary>
    public static ImportResult ParseJobsFromCsv(
        string? csv, IReadOnlyCollection<DownloadJob>? existingJobs = null)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return Failed("The file is empty.");

        List<string[]> rows;
        try
        {
            rows = ParseCsvRows(csv);
        }
        catch (FormatException ex)
        {
            return Failed($"Invalid CSV: {ex.Message}");
        }

        if (rows.Count == 0)
            return Failed("The file is empty.");

        var header = rows[0];
        var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim();
            if (!string.IsNullOrEmpty(name) && !columnIndex.ContainsKey(name))
                columnIndex[name] = i;
        }
        foreach (var required in ExportedFieldNames)
        {
            if (!columnIndex.ContainsKey(required))
                return Failed($"Invalid CSV: missing required column '{required}' in the header row.");
        }

        var dtos = new List<ImportJobDto>();
        for (var r = 1; r < rows.Count; r++)
        {
            var fields = rows[r];
            if (fields.Length == 0 || (fields.Length == 1 && string.IsNullOrEmpty(fields[0])))
                continue; // blank line
            if (fields.Length != header.Length)
                return Failed($"Invalid CSV: record {r} has {fields.Length} fields but the header has {header.Length}.");

            string? Get(string name) => fields[columnIndex[name]];
            dtos.Add(new ImportJobDto(
                Get("url"),
                Get("destinationPath"),
                ParseNullableLong(Get("totalBytes"), r, out var longError) is { } value ? value : null,
                Get("expectedSha256"),
                Get("state"),
                Get("createdAt")));
            if (longError is not null)
                return Failed(longError);
        }

        return ValidateDtos(dtos, existingJobs);
    }

    // ------------------------------------------------------------------
    // Import: parse + validate + persist (only when the whole batch is valid)
    // ------------------------------------------------------------------

    /// <summary>
    /// Validates a JSON batch and, only if the whole batch is valid,
    /// saves each imported job to the journal. On failure nothing is
    /// persisted. Duplicates (same URL + destination as an existing
    /// job) are skipped, not saved twice.
    /// </summary>
    public static async Task<ImportResult> ImportJobsFromJsonAsync(
        string? json,
        IDownloadJournal journal,
        IReadOnlyCollection<DownloadJob>? existingJobs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var result = ParseJobsFromJson(json, existingJobs);
        if (!result.Success)
            return result;
        foreach (var job in result.Imported)
            await journal.SaveJobAsync(job, cancellationToken);
        return result;
    }

    /// <summary>CSV equivalent of <see cref="ImportJobsFromJsonAsync"/>.</summary>
    public static async Task<ImportResult> ImportJobsFromCsvAsync(
        string? csv,
        IDownloadJournal journal,
        IReadOnlyCollection<DownloadJob>? existingJobs = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var result = ParseJobsFromCsv(csv, existingJobs);
        if (!result.Success)
            return result;
        foreach (var job in result.Imported)
            await journal.SaveJobAsync(job, cancellationToken);
        return result;
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    private static ImportResult ValidateDtos(
        List<ImportJobDto> dtos, IReadOnlyCollection<DownloadJob>? existingJobs)
    {
        var errors = new List<string>();
        var imported = new List<DownloadJob>();
        var skipped = new List<DownloadJob>();
        var seen = new HashSet<(string Url, string Destination)>();
        if (existingJobs is not null)
        {
            foreach (var existing in existingJobs)
                seen.Add((existing.Url, existing.DestinationPath));
        }

        for (var i = 0; i < dtos.Count; i++)
        {
            var recordNo = i + 1;
            var dto = dtos[i];
            if (!TryBuildJob(dto, recordNo, out var job, out var error))
            {
                errors.Add(error!);
                continue;
            }

            var key = (job!.Url, job.DestinationPath);
            if (!seen.Add(key))
            {
                skipped.Add(job);
                continue;
            }
            imported.Add(job);
        }

        if (errors.Count > 0)
        {
            // All-or-nothing: a failed batch imports nothing, and
            // duplicates are not reported for a batch that failed.
            return new ImportResult(Array.Empty<DownloadJob>(), Array.Empty<DownloadJob>(), errors);
        }
        return new ImportResult(imported, skipped, Array.Empty<string>());
    }

    private static bool TryBuildJob(
        ImportJobDto dto, int recordNo, out DownloadJob? job, out string? error)
    {
        job = null;
        error = null;

        if (string.IsNullOrWhiteSpace(dto.Url))
        {
            error = $"Record {recordNo}: 'url' is required.";
            return false;
        }
        if (!IsSafeAbsoluteUri(dto.Url, out var urlError))
        {
            error = $"Record {recordNo}: {urlError}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.DestinationPath))
        {
            error = $"Record {recordNo}: 'destinationPath' is required.";
            return false;
        }
        if (!IsSafeDestinationPath(dto.DestinationPath, out var pathError))
        {
            error = $"Record {recordNo}: {pathError}";
            return false;
        }

        if (dto.TotalBytes is < 0)
        {
            error = $"Record {recordNo}: 'totalBytes' must be zero or greater.";
            return false;
        }

        string? sha = null;
        if (!string.IsNullOrWhiteSpace(dto.ExpectedSha256))
        {
            sha = dto.ExpectedSha256.Trim();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
            {
                error = $"Record {recordNo}: 'expectedSha256' must be exactly 64 hexadecimal characters.";
                return false;
            }
        }

        var state = DownloadState.Queued;
        if (!string.IsNullOrWhiteSpace(dto.State) &&
            !Enum.TryParse<DownloadState>(dto.State.Trim(), ignoreCase: true, out state))
        {
            error = $"Record {recordNo}: 'state' value '{dto.State}' is not a known download state.";
            return false;
        }

        var createdAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(dto.CreatedAt) &&
            !DateTimeOffset.TryParse(dto.CreatedAt.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out createdAt))
        {
            error = $"Record {recordNo}: 'createdAt' value '{dto.CreatedAt}' is not a valid date.";
            return false;
        }

        job = new DownloadJob(
            Guid.NewGuid(),
            dto.Url.Trim(),
            dto.DestinationPath,
            dto.TotalBytes,
            sha,
            state,
            createdAt);
        return true;
    }

    /// <summary>
    /// Absolute http/https/ftp URI, with no embedded credentials.
    /// A URL carrying a username/password (userinfo) is rejected: the
    /// job model has nowhere safe to store credentials, and importing
    /// one would silently persist a secret.
    /// </summary>
    private static bool IsSafeAbsoluteUri(string value, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            error = $"'url' value '{value}' is not a valid absolute URI.";
            return false;
        }
        if (uri.Scheme is not "http" and not "https" and not "ftp")
        {
            error = $"'url' scheme '{uri.Scheme}' is not supported; use http, https, or ftp.";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "'url' must not contain embedded credentials (username/password).";
            return false;
        }
        return true;
    }

    /// <summary>
    /// A destination must be a well-formed file path with no control
    /// characters. Absolute paths are accepted as-is and never
    /// silently rewritten. Relative paths are accepted only when they
    /// contain no ".." segment: a relative path that escapes its
    /// directory is rejected rather than normalized or relocated.
    /// </summary>
    private static bool IsSafeDestinationPath(string value, out string? error)
    {
        error = null;
        if (value.Any(char.IsControl))
        {
            error = "'destinationPath' must not contain control characters.";
            return false;
        }
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = "'destinationPath' contains characters that are not valid in a path.";
            return false;
        }
        if (value.EndsWith('/') || value.EndsWith('\\'))
        {
            error = "'destinationPath' must name a file, not a directory.";
            return false;
        }
        if (IsAbsolutePath(value))
            return true;

        var segments = value.Split('/', '\\');
        if (segments.Any(s => s == ".."))
        {
            error = "'destinationPath' is a relative path containing '..' and is rejected.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Absolute on the current OS, plus Windows drive-absolute
    /// (C:\...) and UNC (\\server\share) paths, which are absolute
    /// for this Windows-targeted product even when validated on Linux.
    /// </summary>
    private static bool IsAbsolutePath(string value)
    {
        if (Path.IsPathFullyQualified(value))
            return true;
        if (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
            (value[2] == '\\' || value[2] == '/'))
            return true;
        if (value.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        return false;
    }

    // ------------------------------------------------------------------
    // CSV helpers (RFC 4180)
    // ------------------------------------------------------------------

    /// <summary>
    /// RFC 4180 quoting, plus one hardening rule: a field starting
    /// with a spreadsheet-formula trigger (=, +, -, @) is always
    /// quoted, so a hostile value such as "=cmd|..." is stored as
    /// one quoted data field rather than a bare formula-looking cell.
    /// Quoting marks the value as data in the file; this application
    /// never evaluates it, and import returns it byte-for-byte.
    /// (Note: some spreadsheet applications still interpret a leading
    /// "=" when a CSV is opened directly — open exported files with
    /// the application's data-import function when that matters.)
    /// </summary>
    private static string QuoteCsv(string field)
    {
        if (field.Length > 0 && field[0] is '=' or '+' or '-' or '@')
            return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
            return field;
        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Minimal RFC 4180 parser: quoted fields may contain commas,
    /// doubled quotes, and embedded CR/LF; lines end with LF or CRLF.
    /// An unterminated quoted field is a format error.
    /// </summary>
    private static List<string[]> ParseCsvRows(string csv)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"')
            {
                if (field.Length == 0 && !fieldStarted)
                {
                    inQuotes = true;
                    fieldStarted = true;
                }
                else
                {
                    throw new FormatException("Unexpected '\"' in an unquoted CSV field.");
                }
            }
            else if (ch == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                    i++;
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                rows.Add(fields.ToArray());
                fields.Clear();
            }
            else
            {
                field.Append(ch);
                fieldStarted = true;
            }
        }

        if (inQuotes)
            throw new FormatException("Unterminated quoted field.");
        if (field.Length > 0 || fieldStarted || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add(fields.ToArray());
        }
        return rows;
    }

    private static long? ParseNullableLong(string? value, int recordNo, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"Invalid CSV: record {recordNo} has a 'totalBytes' value '{value}' that is not a whole number.";
            return null;
        }
        return parsed;
    }

    private static ImportResult Failed(string message) =>
        new(Array.Empty<DownloadJob>(), Array.Empty<DownloadJob>(), new[] { message });

    private sealed record ExportedJobDto(
        string Url,
        string DestinationPath,
        long? TotalBytes,
        string? ExpectedSha256,
        string State,
        string CreatedAt);

    private sealed record ImportJobDto(
        string? Url,
        string? DestinationPath,
        long? TotalBytes,
        string? ExpectedSha256,
        string? State,
        string? CreatedAt);

    private static ExportedJobDto ToDto(DownloadJob job) => new(
        job.Url,
        job.DestinationPath,
        job.TotalBytes,
        job.ExpectedSha256,
        job.State.ToString(),
        job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
}

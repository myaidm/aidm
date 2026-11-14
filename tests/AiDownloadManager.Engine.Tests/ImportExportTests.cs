using System.Globalization;
using System.Text.Json;
using AiDownloadManager.Application;
using AiDownloadManager.Core;
using AiDownloadManager.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AiDownloadManager.Engine.Tests;

/// <summary>
/// Import/export tests. Export is verified to emit only the six
/// whitelisted fields and never a credential-like value; import is
/// verified to validate whole batches before anything is persisted
/// (a batch with one bad record imports nothing), to skip duplicates,
/// and to treat hostile CSV content as inert data.
/// </summary>
public class ImportExportTests : IAsyncLifetime
{
    private string _workDir = string.Empty;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "aidm-importexport-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private static DownloadJob Job(
        string url = "https://example.com/files/setup.exe",
        string dest = "/tmp/downloads/setup.exe",
        long? total = 1234567,
        string? sha = null,
        DownloadState state = DownloadState.Completed) =>
        new(Guid.NewGuid(), url, dest, total,
            sha ?? new string('a', 64),
            state, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));

    private SqliteDownloadJournal CreateJournal(string name = "j.db")
    {
        var journal = new SqliteDownloadJournal(Path.Combine(_workDir, name));
        journal.InitializeAsync().GetAwaiter().GetResult();
        return journal;
    }

    private long CountJobsInDb(string dbPath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM downloads";
        return (long)cmd.ExecuteScalar()!;
    }

    // ---------------------------------------------------------------
    // Round-trips
    // ---------------------------------------------------------------

    [Fact]
    public void JsonExport_ThenImport_RoundTripsAllFields()
    {
        var jobs = new[]
        {
            Job(),
            // Built directly: the Job() helper substitutes a dummy
            // SHA-256 when none is given, and this case must test
            // genuinely null TotalBytes / ExpectedSha256.
            new DownloadJob(Guid.NewGuid(), "ftp://files.example.org/pub/data.zip",
                "/tmp/downloads/data.zip", null, null,
                DownloadState.Queued, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero))
        };

        var json = JobImportExport.ExportJobsToJson(jobs);
        var result = JobImportExport.ParseJobsFromJson(json);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(2, result.Imported.Count);
        Assert.Empty(result.SkippedDuplicates);
        for (var i = 0; i < jobs.Length; i++)
        {
            Assert.Equal(jobs[i].Url, result.Imported[i].Url);
            Assert.Equal(jobs[i].DestinationPath, result.Imported[i].DestinationPath);
            Assert.Equal(jobs[i].TotalBytes, result.Imported[i].TotalBytes);
            Assert.Equal(jobs[i].ExpectedSha256, result.Imported[i].ExpectedSha256);
            Assert.Equal(jobs[i].State, result.Imported[i].State);
            Assert.Equal(jobs[i].CreatedAt, result.Imported[i].CreatedAt);
        }
    }

    [Fact]
    public void CsvExport_ThenImport_RoundTripsAllFields()
    {
        // The second job exercises CSV quoting: a comma in the URL and
        // a comma (plus, on Unix, a double quote) in the destination.
        // A double quote is not a legal Windows path character, so the
        // quote case is Unix-only; the comma case runs everywhere.
        var quotedDest = OperatingSystem.IsWindows()
            ? "/tmp/downloads/a,b.bin"
            : "/tmp/downloads/a\"b,c.bin";
        var jobs = new[]
        {
            Job(),
            Job("https://example.com/a,b.bin", quotedDest,
                42, new string('f', 64), DownloadState.Failed)
        };

        var csv = JobImportExport.ExportJobsToCsv(jobs);
        var result = JobImportExport.ParseJobsFromCsv(csv);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(2, result.Imported.Count);
        Assert.Equal(jobs[0].Url, result.Imported[0].Url);
        Assert.Equal(jobs[1].Url, result.Imported[1].Url);
        Assert.Equal(jobs[1].DestinationPath, result.Imported[1].DestinationPath);
        Assert.Equal(42, result.Imported[1].TotalBytes);
        Assert.Equal(new string('f', 64), result.Imported[1].ExpectedSha256);
        Assert.Equal(DownloadState.Failed, result.Imported[1].State);
    }

    // ---------------------------------------------------------------
    // Credential-absence
    // ---------------------------------------------------------------

    [Fact]
    public void JsonExport_ContainsOnlyWhitelistedFields_NoCredentialLikeValues()
    {
        var json = JobImportExport.ExportJobsToJson(new[] { Job() });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(1, root.GetArrayLength());
        var obj = root[0];
        var names = obj.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(JobImportExport.ExportedFieldNames.OrderBy(n => n).ToArray(), names);

        var lower = json.ToLowerInvariant();
        foreach (var forbidden in new[] { "password", "token", "cookie", "authorization",
                     "credential", "secret", "api_key", "apikey", "auth", "bearer", "session" })
        {
            Assert.DoesNotContain(forbidden, lower);
        }
    }

    [Fact]
    public void CsvExport_ContainsOnlyWhitelistedFields_NoCredentialLikeValues()
    {
        var csv = JobImportExport.ExportJobsToCsv(new[] { Job() });

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("url,destinationPath,totalBytes,expectedSha256,state,createdAt", lines[0].TrimEnd('\r'));
        Assert.Equal(2, lines.Length);

        var lower = csv.ToLowerInvariant();
        foreach (var forbidden in new[] { "password", "token", "cookie", "authorization",
                     "credential", "secret", "api_key", "apikey", "auth", "bearer", "session" })
        {
            Assert.DoesNotContain(forbidden, lower);
        }
    }

    // ---------------------------------------------------------------
    // Malformed input: fail safely, import nothing
    // ---------------------------------------------------------------

    [Fact]
    public async Task MalformedJson_ImportsNothing_AndPersistsNothing()
    {
        var journal = CreateJournal();
        var dbPath = Path.Combine(_workDir, "j.db");

        var result = await JobImportExport.ImportJobsFromJsonAsync("{ this is not json", journal);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Imported);
        Assert.Equal(0, CountJobsInDb(dbPath));
    }

    [Fact]
    public async Task MalformedCsv_ImportsNothing_AndPersistsNothing()
    {
        var journal = CreateJournal();
        var dbPath = Path.Combine(_workDir, "j.db");
        // Unterminated quoted field.
        const string csv = "url,destinationPath,totalBytes,expectedSha256,state,createdAt\n" +
            "\"https://example.com/x.bin,/tmp/x.bin,1,,Queued,2026-11-13T12:00:00Z\n";

        var result = await JobImportExport.ImportJobsFromCsvAsync(csv, journal);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Imported);
        Assert.Equal(0, CountJobsInDb(dbPath));
    }

    [Fact]
    public async Task BatchWithOneInvalidRecord_ImportsNothing()
    {
        var journal = CreateJournal();
        var dbPath = Path.Combine(_workDir, "j.db");
        var good = Job();
        var json = JobImportExport.ExportJobsToJson(new[]
        {
            good,
            Job("not a url at all", "/tmp/downloads/bad.bin")
        });

        var result = await JobImportExport.ImportJobsFromJsonAsync(json, journal);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Imported);
        Assert.Equal(0, CountJobsInDb(dbPath));
    }

    [Fact]
    public async Task ValidBatch_PersistsAllJobs()
    {
        var journal = CreateJournal();
        var json = JobImportExport.ExportJobsToJson(new[]
        {
            Job(),
            Job("https://example.com/other.bin", "/tmp/downloads/other.bin")
        });

        var result = await JobImportExport.ImportJobsFromJsonAsync(json, journal);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(2, result.Imported.Count);
        foreach (var imported in result.Imported)
        {
            var loaded = await journal.LoadJobAsync(imported.Id);
            Assert.NotNull(loaded);
            Assert.Equal(imported.Url, loaded!.Url);
        }
    }

    // ---------------------------------------------------------------
    // Validation of individual records
    // ---------------------------------------------------------------

    [Fact]
    public void MissingFields_UseDefaults_AndUnknownFieldsAreIgnored()
    {
        const string json = """
            [
              { "url": "https://example.com/min.bin", "destinationPath": "/tmp/downloads/min.bin",
                "someFutureField": "ignored", "anotherUnknown": 42 }
            ]
            """;

        var result = JobImportExport.ParseJobsFromJson(json);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(1, result.Imported.Count);
        var job = result.Imported[0];
        Assert.Null(job.TotalBytes);
        Assert.Null(job.ExpectedSha256);
        Assert.Equal(DownloadState.Queued, job.State);
        Assert.True(DateTimeOffset.UtcNow - job.CreatedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void InvalidUrls_AreRejected()
    {
        var badUrls = new[]
        {
            "not a url",
            "javascript:alert(1)",
            "file:///etc/passwd",
            "ftp://",
            "https://user:secret@example.com/file.bin", // embedded credentials
            "http://user@example.com/file.bin"
        };

        foreach (var url in badUrls)
        {
            var json = JobImportExport.ExportJobsToJson(new[] { Job(url, "/tmp/downloads/x.bin") });
            var result = JobImportExport.ParseJobsFromJson(json);
            Assert.False(result.Success, $"URL should have been rejected: {url}");
            Assert.Empty(result.Imported);
        }
    }

    [Fact]
    public void InvalidSha256_AndNegativeTotalBytes_AreRejected()
    {
        var tooShort = Job(sha: new string('a', 63));
        var nonHex = Job(sha: new string('g', 64));
        var negative = Job(total: -1);

        foreach (var bad in new[] { tooShort, nonHex, negative })
        {
            var json = JobImportExport.ExportJobsToJson(new[] { bad });
            var result = JobImportExport.ParseJobsFromJson(json);
            Assert.False(result.Success);
            Assert.Empty(result.Imported);
        }

        // Uppercase hex is fine.
        var ok = Job(sha: new string('A', 64));
        Assert.True(JobImportExport.ParseJobsFromJson(
            JobImportExport.ExportJobsToJson(new[] { ok })).Success);
    }

    [Fact]
    public void UnsafeDestinationPaths_AreRejected()
    {
        var badPaths = new[]
        {
            "../escape.bin",                 // relative path escaping with ..
            "subdir/../../escape.bin",      // .. buried in a relative path
            "..\\escape.bin",               // Windows-style ..
            "bad\0name.bin",                // NUL byte
            "bad\nname.bin",                // control character
            "/tmp/downloads/",              // a directory, not a file
        };

        foreach (var dest in badPaths)
        {
            var json = JobImportExport.ExportJobsToJson(new[] { Job(dest: dest) });
            var result = JobImportExport.ParseJobsFromJson(json);
            Assert.False(result.Success, $"Destination should have been rejected: {dest}");
            Assert.Empty(result.Imported);
        }

        // Well-formed absolute paths (Unix, Windows drive, UNC) are
        // accepted as-is and never rewritten.
        foreach (var dest in new[]
                 { "/tmp/downloads/ok.bin", @"C:\Users\ryan\Downloads\ok.bin", @"\\server\share\ok.bin" })
        {
            var json = JobImportExport.ExportJobsToJson(new[] { Job(dest: dest) });
            var result = JobImportExport.ParseJobsFromJson(json);
            Assert.True(result.Success, $"Destination should have been accepted: {dest} — " +
                string.Join("; ", result.Errors));
            Assert.Equal(dest, result.Imported[0].DestinationPath);
        }
    }

    // ---------------------------------------------------------------
    // Duplicates
    // ---------------------------------------------------------------

    [Fact]
    public void DuplicateJobs_AreSkippedAndReported_NotImportedTwice()
    {
        var existing = new[] { Job("https://example.com/a.bin", "/tmp/downloads/a.bin") };
        var batch = new[]
        {
            Job("https://example.com/a.bin", "/tmp/downloads/a.bin"), // duplicate of existing
            Job("https://example.com/b.bin", "/tmp/downloads/b.bin"), // new
            Job("https://example.com/b.bin", "/tmp/downloads/b.bin"), // duplicate within the batch
            Job("https://example.com/a.bin", "/tmp/downloads/other.bin") // same URL, different dest: new
        };

        var result = JobImportExport.ParseJobsFromJson(
            JobImportExport.ExportJobsToJson(batch), existing);

        Assert.True(result.Success);
        Assert.Equal(2, result.Imported.Count);
        Assert.Equal(2, result.SkippedDuplicates.Count);
        Assert.Equal("https://example.com/b.bin", result.Imported[0].Url);
        Assert.Equal("https://example.com/a.bin", result.Imported[1].Url);
    }

    // ---------------------------------------------------------------
    // CSV injection: hostile fields are inert data, never executed
    // ---------------------------------------------------------------

    [Fact]
    public void CsvFormulaField_RoundTripsAsInertData()
    {
        // A classic spreadsheet-formula injection payload. It must
        // survive export/import byte-for-byte as data; this code never
        // evaluates it, and the exporter quotes it so it stays one
        // CSV field.
        var hostile = Job(dest: "=cmd|'/C calc'!A0");
        var csv = JobImportExport.ExportJobsToCsv(new[] { hostile });

        Assert.Contains("\"=cmd|'/C calc'!A0\"", csv);

        var result = JobImportExport.ParseJobsFromCsv(csv);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(1, result.Imported.Count);
        Assert.Equal("=cmd|'/C calc'!A0", result.Imported[0].DestinationPath);
    }

    [Fact]
    public void CsvFieldWithNewlineAndQuotes_IsQuotedAsData_AndRejectedSafelyOnImport()
    {
        // A field containing a newline and quotes: the exporter must
        // keep it inside one quoted field (it must not create a
        // phantom second record), and the importer must treat it as
        // data. A destination containing a control character is not a
        // valid path, so import rejects that record safely — it is
        // never executed, and it does not corrupt the other records.
        var hostile = Job(dest: "we\"ird\nname.bin");
        var csv = JobImportExport.ExportJobsToCsv(new[] { hostile, Job() });

        Assert.Contains("\"we\"\"ird\nname.bin\"", csv);

        var result = JobImportExport.ParseJobsFromCsv(csv);
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Imported); // all-or-nothing: the good record is not imported either
        Assert.Contains("destinationPath", result.Errors[0]);
    }
}

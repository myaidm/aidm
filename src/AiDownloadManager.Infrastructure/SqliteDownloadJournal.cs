using AiDownloadManager.Core;
using Microsoft.Data.Sqlite;

namespace AiDownloadManager.Infrastructure;

/// <summary>
/// SQLite-backed download journal. Uses WAL mode and synchronous FULL
/// so a completed-range record survives a process kill or power loss.
/// Schema is versioned; <see cref="InitializeAsync"/> applies pending
/// migrations in order.
/// </summary>
public sealed class SqliteDownloadJournal : IDownloadJournal
{
    private readonly string _connectionString;

    private static readonly (int Version, string Sql)[] Migrations =
    {
        (1, """
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS downloads (
                id TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                destination TEXT NOT NULL,
                total_bytes INTEGER NULL,
                expected_sha256 TEXT NULL,
                state TEXT NOT NULL,
                created_at TEXT NOT NULL,
                error TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS completed_ranges (
                download_id TEXT NOT NULL,
                start_byte INTEGER NOT NULL,
                end_byte INTEGER NOT NULL,
                PRIMARY KEY (download_id, start_byte)
            );
            """)
    };

    public SqliteDownloadJournal(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (version, sql) in Migrations)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version'";
            var exists = (long)(await check.ExecuteScalarAsync(cancellationToken))! > 0;
            var applied = false;
            if (exists)
            {
                await using var query = connection.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM schema_version WHERE version = $v";
                query.Parameters.AddWithValue("$v", version);
                applied = (long)(await query.ExecuteScalarAsync(cancellationToken))! > 0;
            }

            if (!applied)
            {
                await using var tx = await connection.BeginTransactionAsync(cancellationToken);
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                await using var mark = connection.CreateCommand();
                mark.Transaction = (SqliteTransaction)tx;
                mark.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES ($v, $at)";
                mark.Parameters.AddWithValue("$v", version);
                mark.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                await mark.ExecuteNonQueryAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
        }
    }

    public async Task SaveJobAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO downloads (id, url, destination, total_bytes, expected_sha256, state, created_at, error)
            VALUES ($id, $url, $dest, $total, $hash, $state, $created, NULL)
            ON CONFLICT(id) DO UPDATE SET
                url = excluded.url, destination = excluded.destination,
                total_bytes = excluded.total_bytes, expected_sha256 = excluded.expected_sha256,
                state = excluded.state
            """;
        cmd.Parameters.AddWithValue("$id", job.Id.ToString());
        cmd.Parameters.AddWithValue("$url", job.Url);
        cmd.Parameters.AddWithValue("$dest", job.DestinationPath);
        cmd.Parameters.AddWithValue("$total", job.TotalBytes is null ? DBNull.Value : job.TotalBytes.Value);
        cmd.Parameters.AddWithValue("$hash", job.ExpectedSha256 is null ? DBNull.Value : job.ExpectedSha256);
        cmd.Parameters.AddWithValue("$state", job.State.ToString());
        cmd.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DownloadJob?> LoadJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT url, destination, total_bytes, expected_sha256, state, created_at FROM downloads WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new DownloadJob(
            id,
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            Enum.Parse<DownloadState>(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5)));
    }

    public async Task<IReadOnlyList<DownloadJob>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, url, destination, total_bytes, expected_sha256, state, created_at FROM downloads ORDER BY created_at, id";
        var jobs = new List<DownloadJob>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(new DownloadJob(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                Enum.Parse<DownloadState>(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return jobs;
    }

    public async Task DeleteJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteRanges = connection.CreateCommand())
        {
            deleteRanges.Transaction = (SqliteTransaction)tx;
            deleteRanges.CommandText = "DELETE FROM completed_ranges WHERE download_id = $id";
            deleteRanges.Parameters.AddWithValue("$id", id.ToString());
            await deleteRanges.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var deleteJob = connection.CreateCommand())
        {
            deleteJob.Transaction = (SqliteTransaction)tx;
            deleteJob.CommandText = "DELETE FROM downloads WHERE id = $id";
            deleteJob.Parameters.AddWithValue("$id", id.ToString());
            await deleteJob.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    public async Task UpdateJobStateAsync(Guid id, DownloadState state, string? errorMessage, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE downloads SET state = $state, error = $error WHERE id = $id";
        cmd.Parameters.AddWithValue("$state", state.ToString());
        cmd.Parameters.AddWithValue("$error", errorMessage is null ? DBNull.Value : errorMessage);
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCompletedRangesAsync(Guid id, IReadOnlyList<ByteRange> completed, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)tx;
            delete.CommandText = "DELETE FROM completed_ranges WHERE download_id = $id";
            delete.Parameters.AddWithValue("$id", id.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var range in SegmentPlan.MergeRanges(completed))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = "INSERT INTO completed_ranges (download_id, start_byte, end_byte) VALUES ($id, $start, $end)";
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.Parameters.AddWithValue("$start", range.Start);
            insert.Parameters.AddWithValue("$end", range.End);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ByteRange>> LoadCompletedRangesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT start_byte, end_byte FROM completed_ranges WHERE download_id = $id ORDER BY start_byte";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var ranges = new List<ByteRange>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ranges.Add(new ByteRange(reader.GetInt64(0), reader.GetInt64(1)));
        return ranges;
    }
}

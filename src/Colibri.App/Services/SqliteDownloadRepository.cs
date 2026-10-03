using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using Colibri.Core.Abstractions;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Microsoft.Data.Sqlite;

namespace Colibri.App.Services;

/// <summary>
/// Stores downloads in a SQLite file with hand-written SQL. The schema version is kept in
/// <c>PRAGMA user_version</c> so later versions can migrate old files.
/// </summary>
/// <remarks>
/// Microsoft.Data.Sqlite's async methods actually run synchronously, so every call is moved to the
/// thread pool to keep disk work off the UI thread.
/// </remarks>
public sealed class SqliteDownloadRepository : IDownloadRepository, IDownloadCredentialRepository
{
    private const int SchemaVersion = 3;

    private const string Columns =
        "id, url, final_url, file_name, save_folder, category, state, total_bytes, completed_bytes, download_speed, " +
        "connections, engine_id, engine_handle, referrer, user_agent, headers, added_at, completed_at, error_message, speed_limit, connection_limit, protected_headers";

    private readonly string _connectionString;
    private readonly ICredentialProtector _credentialProtector;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteDownloadRepository(string databasePath, ICredentialProtector credentialProtector)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _credentialProtector = credentialProtector;
    }

    public Task<IReadOnlyList<DownloadItem>> GetAllAsync(CancellationToken ct) =>
        RunAsync<IReadOnlyList<DownloadItem>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM downloads ORDER BY added_at";
            using var reader = command.ExecuteReader();
            var items = new List<DownloadItem>();
            while (reader.Read())
            {
                items.Add(Read(reader));
            }

            return items;
        }, ct);

    public Task<DownloadItem?> GetAsync(Guid id, CancellationToken ct) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM downloads WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }, ct);

    public Task AddAsync(DownloadItem item, CancellationToken ct) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO downloads ({Columns})
                VALUES ($id, $url, $final_url, $file_name, $save_folder, $category, $state, $total_bytes, $completed_bytes,
                        $download_speed, $connections, $engine_id, $engine_handle, $referrer, $user_agent, $headers,
                        $added_at, $completed_at, $error_message, $speed_limit, $connection_limit, $protected_headers)
                """;
            Bind(command, item);
            return command.ExecuteNonQuery();
        }, ct);

    public Task UpdateAsync(DownloadItem item, CancellationToken ct) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE downloads SET
                    url = $url, final_url = $final_url, file_name = $file_name, save_folder = $save_folder,
                    category = $category, state = $state, total_bytes = $total_bytes, completed_bytes = $completed_bytes,
                    download_speed = $download_speed, connections = $connections, engine_id = $engine_id,
                    engine_handle = $engine_handle, referrer = $referrer, user_agent = $user_agent, headers = $headers,
                    added_at = $added_at, completed_at = $completed_at, error_message = $error_message,
                    speed_limit = $speed_limit, connection_limit = $connection_limit, protected_headers = $protected_headers
                WHERE id = $id
                """;
            Bind(command, item);
            return command.ExecuteNonQuery();
        }, ct);

    public Task DeleteAsync(Guid id, CancellationToken ct) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM downloads WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            return command.ExecuteNonQuery();
        }, ct);

    public Task ClearCredentialsAsync(Guid downloadId, CancellationToken ct) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE downloads SET headers = '{}', protected_headers = NULL WHERE id = $id";
            command.Parameters.AddWithValue("$id", downloadId.ToString());
            return command.ExecuteNonQuery();
        }, ct);

    private async Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        return await Task.Run(() =>
        {
            using var connection = Open();
            return work(connection);
        }, ct);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA secure_delete = ON");
        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (!_initialized)
            {
                await Task.Run(Migrate, ct);
                _initialized = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void Migrate()
    {
        using var connection = Open();

        // Write-ahead logging: readers do not block the writer, and a crash mid-write cannot corrupt the
        // file. The mode is stored in the database file, so setting it once is enough.
        Execute(connection, "PRAGMA journal_mode = WAL");

        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"The download database was created by a newer Colibri (schema {version}, this version knows {SchemaVersion}).");
        }

        using (var transaction = connection.BeginTransaction())
        {
            if (version < 1)
            {
                Execute(connection, """
                    CREATE TABLE downloads (
                        id              TEXT PRIMARY KEY NOT NULL,
                        url             TEXT NOT NULL,
                        final_url       TEXT,
                        file_name       TEXT NOT NULL,
                        save_folder     TEXT NOT NULL,
                        category        TEXT NOT NULL,
                        state           TEXT NOT NULL,
                        total_bytes     INTEGER,
                        completed_bytes INTEGER NOT NULL,
                        download_speed  INTEGER NOT NULL,
                        connections     INTEGER NOT NULL,
                        engine_id       TEXT NOT NULL,
                        engine_handle   TEXT,
                        referrer        TEXT,
                        user_agent      TEXT,
                        headers         TEXT NOT NULL,
                        added_at        TEXT NOT NULL,
                        completed_at    TEXT,
                        error_message   TEXT
                    )
                    """, transaction);

                // PRAGMA does not accept parameters; the value is our own constant.
                Execute(connection, "PRAGMA user_version = 1", transaction);
            }

            if (version < 2)
            {
                Execute(connection, "ALTER TABLE downloads ADD COLUMN speed_limit INTEGER", transaction);
                Execute(connection, "ALTER TABLE downloads ADD COLUMN connection_limit INTEGER", transaction);
                Execute(connection, "PRAGMA user_version = 2", transaction);
            }

            if (version < 3)
            {
                Execute(connection, "ALTER TABLE downloads ADD COLUMN protected_headers BLOB", transaction);
                var rows = new List<(Guid Id, string Headers, bool Completed)>();
                using (var read = connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT id, headers, state FROM downloads";
                    using var reader = read.ExecuteReader();
                    while (reader.Read())
                        rows.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2) == nameof(DownloadState.Completed)));
                }
                foreach (var row in rows)
                {
                    var headers = row.Completed ? HttpHeaders.Create() : DeserializeHeaders(row.Headers);
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE downloads SET headers = '{}', protected_headers = $protected WHERE id = $id";
                    update.Parameters.AddWithValue("$id", row.Id.ToString());
                    update.Parameters.AddWithValue("$protected", (object?)ProtectHeaders(headers, row.Id) ?? DBNull.Value);
                    update.ExecuteNonQuery();
                }
                Execute(connection, "PRAGMA user_version = 3", transaction);
            }
            transaction.Commit();
        }

        // Rebuild pages and truncate the WAL after migration, including a previous interrupted cleanup.
        // This removes live-file legacy plaintext; backups and filesystem snapshots remain outside our control.
        Checkpoint(connection);
        Execute(connection, "VACUUM");
        Checkpoint(connection);
    }

    private static void Checkpoint(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var result = command.ExecuteReader();
        if (!result.Read() || result.GetInt32(0) != 0)
            throw new InvalidOperationException("Credential migration cleanup could not finish because the download database is in use.");
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void Bind(SqliteCommand command, DownloadItem item)
    {
        var p = command.Parameters;
        p.AddWithValue("$id", item.Id.ToString());
        p.AddWithValue("$url", item.Url);
        p.AddWithValue("$final_url", (object?)item.FinalUrl ?? DBNull.Value);
        p.AddWithValue("$file_name", item.FileName);
        p.AddWithValue("$save_folder", item.SaveFolder);

        // Enums are stored by name, so the file stays readable and reordering the enum cannot corrupt it.
        p.AddWithValue("$category", item.Category.ToString());
        p.AddWithValue("$state", item.State.ToString());
        p.AddWithValue("$total_bytes", (object?)item.TotalBytes ?? DBNull.Value);
        p.AddWithValue("$completed_bytes", item.CompletedBytes);
        p.AddWithValue("$download_speed", item.DownloadSpeed);
        p.AddWithValue("$connections", item.Connections);
        p.AddWithValue("$engine_id", item.EngineId);
        p.AddWithValue("$engine_handle", (object?)item.EngineHandle ?? DBNull.Value);
        p.AddWithValue("$referrer", (object?)item.Referrer ?? DBNull.Value);
        p.AddWithValue("$user_agent", (object?)item.UserAgent ?? DBNull.Value);
        p.AddWithValue("$headers", "{}");
        p.AddWithValue("$protected_headers", item.State == DownloadState.Completed
            ? DBNull.Value : (object?)ProtectHeaders(item.Headers, item.Id) ?? DBNull.Value);
        p.AddWithValue("$added_at", item.AddedAt.ToString("o", CultureInfo.InvariantCulture));
        p.AddWithValue("$completed_at", (object?)item.CompletedAt?.ToString("o", CultureInfo.InvariantCulture) ?? DBNull.Value);
        p.AddWithValue("$error_message", (object?)item.ErrorMessage ?? DBNull.Value);
        p.AddWithValue("$speed_limit", (object?)item.TransferOptions?.SpeedLimitBytesPerSecond ?? DBNull.Value);
        p.AddWithValue("$connection_limit", (object?)item.TransferOptions?.ConnectionsPerServer ?? DBNull.Value);
    }

    private DownloadItem Read(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        Url = r.GetString(1),
        FinalUrl = NullableString(r, 2),
        FileName = r.GetString(3),
        SaveFolder = r.GetString(4),
        Category = Enum.TryParse<DownloadCategory>(r.GetString(5), out var category) ? category : DownloadCategory.Other,
        State = Enum.TryParse<DownloadState>(r.GetString(6), out var state) ? state : DownloadState.Paused,
        TotalBytes = r.IsDBNull(7) ? null : r.GetInt64(7),
        CompletedBytes = r.GetInt64(8),
        DownloadSpeed = r.GetInt64(9),
        Connections = r.GetInt32(10),
        EngineId = r.GetString(11),
        EngineHandle = NullableString(r, 12),
        Referrer = NullableString(r, 13),
        UserAgent = NullableString(r, 14),
        Headers = ReadHeaders(r),
        AddedAt = ParseDate(r.GetString(16)),
        CompletedAt = r.IsDBNull(17) ? null : ParseDate(r.GetString(17)),
        ErrorMessage = NullableString(r, 18),
        TransferOptions = r.IsDBNull(19) || r.IsDBNull(20) ? null : new(r.GetInt64(19), r.GetInt32(20)),
    };

    private byte[]? ProtectHeaders(Dictionary<string, string> headers, Guid id)
    {
        if (headers.Count == 0) return null;
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(headers);
        try { return _credentialProtector.Protect(plaintext, id); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private Dictionary<string, string> ReadHeaders(SqliteDataReader reader)
    {
        // Never accept a plaintext payload after migration, even if another writer changed the row.
        if (reader.GetString(15) != "{}") throw new CredentialProtectionException();
        if (reader.IsDBNull(21)) return HttpHeaders.Create();
        var plaintext = _credentialProtector.Unprotect((byte[])reader[21], Guid.Parse(reader.GetString(0)));
        try
        {
            return HttpHeaders.Copy(JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext) ?? []);
        }
        catch (JsonException) { throw new CredentialProtectionException(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static Dictionary<string, string> DeserializeHeaders(string json)
    {
        try { return HttpHeaders.Copy(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []); }
        catch (JsonException) { throw new CredentialProtectionException(); }
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ParseDate(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
}

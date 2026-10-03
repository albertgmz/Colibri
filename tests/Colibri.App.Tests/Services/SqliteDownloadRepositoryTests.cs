using Colibri.App.Services;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Colibri.Core.Tests.Fakes;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace Colibri.App.Tests.Services;

public sealed class SqliteDownloadRepositoryTests : IDisposable
{
    private readonly TempAppPaths _paths = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // Pooled connections keep the file open on Windows; release them so the folder can be deleted.
        SqliteConnection.ClearAllPools();
        _paths.Dispose();
    }

    private SqliteDownloadRepository Create(ICredentialProtector? protector = null) => new(_paths.DatabasePath, protector ?? new TestProtector());

    private static DownloadItem FullItem() => new()
    {
        Url = "https://example.com/files/a%20b.zip?token=1",
        FinalUrl = "https://cdn.example.com/a.zip",
        FileName = "a b (1).zip",
        SaveFolder = @"C:\Users\ana\Downloads\Compressed",
        Category = DownloadCategory.Compressed,
        State = DownloadState.Paused,
        TotalBytes = 10_000_000_000,
        CompletedBytes = 4_000_000_000,
        DownloadSpeed = 123_456,
        Connections = 8,
        EngineId = "aria2",
        EngineHandle = "0123456789abcdef",
        Referrer = "https://example.com/page",
        UserAgent = "Mozilla/5.0",
        Headers = HttpHeaders.Copy([new("Cookie", "session=1; ñ=2"), new("Authorization", "Bearer x")]),
        AddedAt = new DateTimeOffset(2026, 10, 1, 12, 30, 45, 123, TimeSpan.FromHours(2)),
        CompletedAt = new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero),
        ErrorMessage = "Something failed",
        TransferOptions = new(4096, 4),
    };

    private static void AssertSame(DownloadItem expected, DownloadItem? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Url, actual.Url);
        Assert.Equal(expected.FinalUrl, actual.FinalUrl);
        Assert.Equal(expected.FileName, actual.FileName);
        Assert.Equal(expected.SaveFolder, actual.SaveFolder);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.TotalBytes, actual.TotalBytes);
        Assert.Equal(expected.CompletedBytes, actual.CompletedBytes);
        Assert.Equal(expected.DownloadSpeed, actual.DownloadSpeed);
        Assert.Equal(expected.Connections, actual.Connections);
        Assert.Equal(expected.EngineId, actual.EngineId);
        Assert.Equal(expected.EngineHandle, actual.EngineHandle);
        Assert.Equal(expected.Referrer, actual.Referrer);
        Assert.Equal(expected.UserAgent, actual.UserAgent);
        Assert.Equal(expected.Headers.OrderBy(h => h.Key), actual.Headers.OrderBy(h => h.Key));
        Assert.Equal(expected.AddedAt, actual.AddedAt);
        Assert.Equal(expected.AddedAt.Offset, actual.AddedAt.Offset);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
        Assert.Equal(expected.ErrorMessage, actual.ErrorMessage);
        Assert.Equal(expected.TransferOptions, actual.TransferOptions);
    }

    [Fact]
    public async Task Every_field_round_trips()
    {
        var item = FullItem();
        var repository = Create();

        await repository.AddAsync(item, Ct);

        AssertSame(item, await repository.GetAsync(item.Id, Ct));
    }

    [Fact]
    public async Task Null_fields_round_trip_as_null()
    {
        var item = new DownloadItem { Url = "https://example.com/a", FileName = "a", SaveFolder = "/tmp", EngineId = "aria2" };
        var repository = Create();

        await repository.AddAsync(item, Ct);

        AssertSame(item, await repository.GetAsync(item.Id, Ct));
    }

    [Fact]
    public async Task Headers_keep_case_insensitive_names_after_loading()
    {
        var item = FullItem();
        var repository = Create();
        await repository.AddAsync(item, Ct);

        var loaded = await repository.GetAsync(item.Id, Ct);

        Assert.Equal("session=1; ñ=2", loaded!.Headers["cookie"]);
    }

    [Fact]
    public async Task Update_changes_the_stored_row()
    {
        var item = FullItem();
        var repository = Create();
        await repository.AddAsync(item, Ct);

        item.State = DownloadState.Completed;
        item.CompletedBytes = item.TotalBytes!.Value;
        item.ErrorMessage = null;
        item.EngineHandle = null;
        await repository.UpdateAsync(item, Ct);

        item.Headers.Clear(); // Completed history no longer carries request credentials.
        AssertSame(item, await repository.GetAsync(item.Id, Ct));
    }

    [Fact]
    public async Task Delete_removes_only_that_row()
    {
        var first = FullItem();
        var second = FullItem();
        second.Id = Guid.NewGuid();
        var repository = Create();
        await repository.AddAsync(first, Ct);
        await repository.AddAsync(second, Ct);

        await repository.DeleteAsync(first.Id, Ct);

        Assert.Null(await repository.GetAsync(first.Id, Ct));
        Assert.Equal([second.Id], (await repository.GetAllAsync(Ct)).Select(i => i.Id));
    }

    [Fact]
    public async Task Data_survives_reopening_the_file()
    {
        var item = FullItem();
        await Create().AddAsync(item, Ct);
        SqliteConnection.ClearAllPools();

        var reopened = Create();

        AssertSame(item, Assert.Single(await reopened.GetAllAsync(Ct)));
    }

    [Fact]
    public async Task A_new_file_gets_schema_version_3_and_wal_mode()
    {
        await Create().GetAllAsync(Ct);

        await using var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, await command.ExecuteScalarAsync(Ct));
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task A_file_from_a_newer_version_is_refused()
    {
        await using (var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            await command.ExecuteNonQueryAsync(Ct);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().GetAllAsync(Ct));
    }

    [Fact]
    public async Task Version_1_migration_preserves_downloads_and_leaves_overrides_unset()
    {
        var item = FullItem();
        item.TransferOptions = null;
        await Create().AddAsync(item, Ct);
        await using (var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE downloads DROP COLUMN speed_limit; ALTER TABLE downloads DROP COLUMN connection_limit; ALTER TABLE downloads DROP COLUMN protected_headers; UPDATE downloads SET headers = $headers; PRAGMA user_version = 1;";
            command.Parameters.AddWithValue("$headers", JsonSerializer.Serialize(item.Headers));
            await command.ExecuteNonQueryAsync(Ct);
        }
        AssertSame(item, await Create().GetAsync(item.Id, Ct));
    }

    [Fact]
    public async Task Header_credentials_are_encrypted_and_bound_to_the_download()
    {
        var first = FullItem();
        var second = FullItem();
        var repository = Create();
        await repository.AddAsync(first, Ct);
        await repository.AddAsync(second, Ct);
        await using var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT headers, protected_headers FROM downloads WHERE id = $id";
        command.Parameters.AddWithValue("$id", first.Id.ToString());
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal("{}", reader.GetString(0));
            Assert.DoesNotContain("session=1", System.Text.Encoding.UTF8.GetString((byte[])reader[1]));
        }
        command.CommandText = "UPDATE downloads SET protected_headers = (SELECT protected_headers FROM downloads WHERE id = $id) WHERE id = $other";
        command.Parameters.AddWithValue("$other", second.Id.ToString());
        await command.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<CredentialProtectionException>(() => repository.GetAsync(second.Id, Ct));
    }

    [Fact]
    public async Task Unavailable_protector_never_persists_plaintext()
    {
        var repository = Create(new FailingProtector());
        var item = FullItem();
        await Assert.ThrowsAsync<CredentialProtectionException>(() => repository.AddAsync(item, Ct));
        Assert.Empty(await repository.GetAllAsync(Ct));
        item.Headers.Clear();
        await repository.AddAsync(item, Ct);
        Assert.NotNull(await repository.GetAsync(item.Id, Ct));
    }

    [Fact]
    public async Task Failed_protection_leaves_an_existing_row_unchanged()
    {
        var item = FullItem();
        await Create().AddAsync(item, Ct);
        var changed = item.Clone();
        changed.FileName = "changed.zip";
        changed.Headers["Cookie"] = "session=changed";
        await Assert.ThrowsAsync<CredentialProtectionException>(() => Create(new FailingProtector()).UpdateAsync(changed, Ct));
        AssertSame(item, await Create().GetAsync(item.Id, Ct));
    }

    [Fact]
    public async Task Completion_and_explicit_cleanup_discard_credentials_without_unlocking()
    {
        var item = FullItem();
        await Create().AddAsync(item, Ct);
        var locked = Create(new FailingProtector());
        item.State = DownloadState.Completed;
        await locked.UpdateAsync(item, Ct);
        Assert.Empty((await locked.GetAsync(item.Id, Ct))!.Headers);
        item.State = DownloadState.Paused;
        await Create().UpdateAsync(item, Ct);
        await locked.ClearCredentialsAsync(item.Id, Ct);
        Assert.Empty((await locked.GetAsync(item.Id, Ct))!.Headers);
        await locked.DeleteAsync(item.Id, Ct);
        Assert.Null(await locked.GetAsync(item.Id, Ct));
    }

    [Fact]
    public async Task Legacy_migration_is_atomic_when_protection_fails()
    {
        var item = FullItem();
        await SeedVersion2Async(item);
        await Assert.ThrowsAsync<CredentialProtectionException>(() => Create(new FailingProtector()).GetAllAsync(Ct));
        await using var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, await command.ExecuteScalarAsync(Ct));
        command.CommandText = "SELECT headers FROM downloads";
        Assert.Equal(JsonSerializer.Serialize(item.Headers), await command.ExecuteScalarAsync(Ct));
        AssertSame(item, await Create().GetAsync(item.Id, Ct));
    }

    [Fact]
    public async Task Legacy_completed_rows_drop_plaintext_without_protection()
    {
        var item = FullItem();
        item.State = DownloadState.Completed;
        await SeedVersion2Async(item);
        Assert.Empty(Assert.Single(await Create(new FailingProtector()).GetAllAsync(Ct)).Headers);
        await using var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT headers FROM downloads";
        Assert.Equal("{}", await command.ExecuteScalarAsync(Ct));
        command.CommandText = "SELECT protected_headers FROM downloads";
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Legacy_migration_removes_plaintext_from_live_database_pages()
    {
        var item = FullItem();
        item.Headers["Cookie"] = "session=unique-legacy-credential-for-migration-test";
        await SeedVersion2Async(item);
        AssertSame(item, await Create().GetAsync(item.Id, Ct));
        SqliteConnection.ClearAllPools();
        var database = await File.ReadAllBytesAsync(_paths.DatabasePath, Ct);
        Assert.DoesNotContain("unique-legacy-credential-for-migration-test", System.Text.Encoding.UTF8.GetString(database));
        if (File.Exists(_paths.DatabasePath + "-wal"))
        {
            var wal = await File.ReadAllBytesAsync(_paths.DatabasePath + "-wal", Ct);
            Assert.DoesNotContain("unique-legacy-credential-for-migration-test", System.Text.Encoding.UTF8.GetString(wal));
        }
    }

    private async Task SeedVersion2Async(DownloadItem item)
    {
        await Create().AddAsync(item, Ct);
        await using var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE downloads DROP COLUMN protected_headers; UPDATE downloads SET headers = $headers; PRAGMA user_version = 2;";
        command.Parameters.AddWithValue("$headers", JsonSerializer.Serialize(item.Headers));
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed class FailingProtector : ICredentialProtector
    {
        public byte[] Protect(byte[] plaintext, Guid downloadId) => throw new CredentialProtectionException();
        public byte[] Unprotect(byte[] ciphertext, Guid downloadId) => throw new CredentialProtectionException();
    }

    // An authenticated test provider, independent of Windows and stable across repository instances.
    private sealed class TestProtector : ICredentialProtector
    {
        private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] plaintext, Guid downloadId)
        {
            var result = new byte[28 + plaintext.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16), downloadId.ToByteArray());
            return result;
        }
        public byte[] Unprotect(byte[] ciphertext, Guid downloadId)
        {
            try
            {
                var result = new byte[ciphertext.Length - 28];
                using var aes = new AesGcm(Key, 16);
                aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), result, downloadId.ToByteArray());
                return result;
            }
            catch (CryptographicException) { throw new CredentialProtectionException(); }
        }
    }
}

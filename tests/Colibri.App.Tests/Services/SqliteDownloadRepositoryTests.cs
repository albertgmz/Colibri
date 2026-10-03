using Colibri.App.Services;
using Colibri.Core.Models;
using Colibri.Core.Tests.Fakes;
using Microsoft.Data.Sqlite;

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

    private SqliteDownloadRepository Create() => new(_paths.DatabasePath);

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
    public async Task A_new_file_gets_schema_version_1_and_wal_mode()
    {
        await Create().GetAllAsync(Ct);

        await using var connection = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
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
}

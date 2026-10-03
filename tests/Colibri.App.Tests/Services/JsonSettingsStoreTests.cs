using Colibri.App.Services;
using Colibri.Core.Models;
using Colibri.Core.Settings;
using Colibri.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;

namespace Colibri.App.Tests.Services;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempAppPaths _paths = new();
    private readonly ListLogger<JsonSettingsStore> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _paths.Dispose();

    private JsonSettingsStore Create() => new(_paths.SettingsPath, _logger);

    [Fact]
    public async Task A_missing_file_gives_the_defaults_without_a_warning()
    {
        var settings = await Create().LoadAsync(Ct);

        Assert.Equal(new AppSettings().MaxConcurrentDownloads, settings.MaxConcurrentDownloads);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Saved_settings_load_back_and_the_file_is_indented_with_enum_names()
    {
        var store = Create();
        var settings = new AppSettings
        {
            Theme = AppTheme.Dark,
            MaxConcurrentDownloads = 5,
            DefaultDownloadFolder = "/data/dl",
            CategoryFolders = { [DownloadCategory.Video] = "/data/videos" },
            Aria2Path = "/opt/aria2c",
        };

        await store.SaveAsync(settings, Ct);
        var loaded = await store.LoadAsync(Ct);

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(5, loaded.MaxConcurrentDownloads);
        Assert.Equal("/data/dl", loaded.DefaultDownloadFolder);
        Assert.Equal("/data/videos", loaded.CategoryFolders[DownloadCategory.Video]);
        Assert.Equal("/opt/aria2c", loaded.Aria2Path);

        var text = await File.ReadAllTextAsync(_paths.SettingsPath, Ct);
        Assert.Contains("\n", text);
        Assert.Contains("\"Dark\"", text);
        Assert.Contains("\"Video\"", text);
        Assert.False(File.Exists(_paths.SettingsPath + ".tmp"));
    }

    [Fact]
    public async Task Saving_twice_overwrites_the_file()
    {
        var store = Create();
        await store.SaveAsync(new AppSettings { MaxConcurrentDownloads = 1 }, Ct);

        await store.SaveAsync(new AppSettings { MaxConcurrentDownloads = 2 }, Ct);

        Assert.Equal(2, (await store.LoadAsync(Ct)).MaxConcurrentDownloads);
    }

    [Fact]
    public async Task A_corrupt_file_gives_the_defaults_and_logs_a_warning()
    {
        await File.WriteAllTextAsync(_paths.SettingsPath, "{ this is not json", Ct);

        var settings = await Create().LoadAsync(Ct);

        Assert.Equal(new AppSettings().ConnectionsPerServer, settings.ConnectionsPerServer);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Nulls_in_a_hand_edited_file_become_defaults()
    {
        await File.WriteAllTextAsync(_paths.SettingsPath, """{ "CategoryFolders": null, "BrowserCaptureExtensions": null, "Aria2Path": null }""", Ct);

        var settings = await Create().LoadAsync(Ct);

        Assert.NotNull(settings.CategoryFolders);
        Assert.NotEmpty(settings.BrowserCaptureExtensions);
        Assert.Equal(string.Empty, settings.Aria2Path);
    }
}

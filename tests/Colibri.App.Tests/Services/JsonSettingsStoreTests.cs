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

    [Theory]
    [InlineData("null", "system")]
    [InlineData("\"\"", "system")]
    [InlineData("\"future-language\"", "system")]
    [InlineData("\" FR-ca \"", "fr-CA")]
    [InlineData("12", "system")]
    [InlineData("true", "system")]
    [InlineData("{}", "system")]
    [InlineData("[]", "system")]
    public async Task Language_tokens_normalize_without_resetting_other_preferences(string token, string expected)
    {
        await File.WriteAllTextAsync(_paths.SettingsPath, "{\"Theme\":\"Dark\",\"BackgroundPalette\":\"ocean\",\"ConnectionsPerServer\":7,\"Language\":" + token + "}", Ct);
        var store = Create();
        var settings = await store.LoadAsync(Ct);
        Assert.Equal(expected, settings.Language);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal("ocean", settings.BackgroundPalette);
        Assert.Equal(7, settings.ConnectionsPerServer);
        Assert.Empty(_logger.Entries);
        await store.SaveAsync(settings, Ct);
        Assert.Equal(expected, (await store.LoadAsync(Ct)).Language);
    }

    [Theory]
    [InlineData("warm")]
    [InlineData("graphite")]
    [InlineData("ocean")]
    [InlineData("forest")]
    public async Task Background_palettes_round_trip_independently(string palette)
    {
        var store = Create();
        await store.SaveAsync(new AppSettings { BackgroundPalette = palette, Theme = AppTheme.Dark, AccentColor = "#0078D4" }, Ct);
        var loaded = await store.LoadAsync(Ct);
        Assert.Equal(palette, loaded.BackgroundPalette);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal("#0078D4", loaded.AccentColor);
    }

    [Theory]
    [InlineData("null", "warm")]
    [InlineData("\"\"", "warm")]
    [InlineData("\"future\"", "warm")]
    [InlineData("\" OCEAN \"", "ocean")]
    [InlineData("12", "warm")]
    [InlineData("true", "warm")]
    [InlineData("{}", "warm")]
    [InlineData("[]", "warm")]
    public async Task Invalid_palette_tokens_preserve_other_settings(string token, string expected)
    {
        await File.WriteAllTextAsync(_paths.SettingsPath, "{\"Theme\":\"Dark\",\"AccentColor\":\"#0078D4\",\"ConnectionsPerServer\":7,\"BackgroundPalette\":" + token + "}", Ct);
        var settings = await Create().LoadAsync(Ct);
        Assert.Equal(expected, settings.BackgroundPalette);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal("#0078D4", settings.AccentColor);
        Assert.Equal(7, settings.ConnectionsPerServer);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task V1_file_and_invalid_layout_load_without_losing_download_settings()
    {
        await File.WriteAllTextAsync(_paths.SettingsPath,
            """{"ConnectionsPerServer":16,"CategoryFolders":{"Music":"D:/Music"},"Layout":{"Width":1,"Height":-20,"Columns":null}}""", Ct);
        var settings = await Create().LoadAsync(Ct);
        Assert.Equal(16, settings.ConnectionsPerServer);
        Assert.Equal("warm", settings.BackgroundPalette);
        Assert.Equal("D:/Music", settings.CategoryFolders[DownloadCategory.Music]);
        Assert.Equal(640, settings.Layout.Width);
        Assert.Equal(400, settings.Layout.Height);
        Assert.Empty(settings.Layout.Columns);
        await Create().SaveAsync(settings, Ct);
        Assert.Equal("D:/Music", (await Create().LoadAsync(Ct)).CategoryFolders[DownloadCategory.Music]);
    }

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

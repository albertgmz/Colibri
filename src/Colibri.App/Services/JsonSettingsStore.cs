using System.Text.Json;
using System.Text.Json.Serialization;
using Colibri.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Colibri.App.Services;

/// <summary>
/// Keeps <see cref="AppSettings"/> in an indented JSON file. A missing or unreadable file gives the
/// defaults (and a warning in the log) instead of an error.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,

        // Enums (theme, category keys) are written by name, which keeps the file readable and editable.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public JsonSettingsStore(string path, ILogger<JsonSettingsStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct);
            return Normalize(settings ?? new AppSettings());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not read the settings file {Path}; using the defaults", _path);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

            // Write a temporary file next to the real one, then swap it in. A crash or full disk while
            // writing leaves the old settings intact instead of a half-written file.
            var temporary = _path + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct);

                // On disk before the swap, so a power cut cannot leave an empty file behind.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>A file edited by hand may contain nulls where the code expects collections.</summary>
    private static AppSettings Normalize(AppSettings settings)
    {
        var defaults = new AppSettings();
        settings.CategoryFolders ??= defaults.CategoryFolders;
        settings.BrowserCaptureExtensions ??= defaults.BrowserCaptureExtensions;
        settings.DefaultDownloadFolder ??= defaults.DefaultDownloadFolder;
        settings.Aria2Path ??= defaults.Aria2Path;
        settings.Layout ??= new WindowLayout();
        settings.Layout.Normalize();
        return settings;
    }
}

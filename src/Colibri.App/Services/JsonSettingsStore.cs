using System.Text.Json;
using System.Text.Json.Serialization;
using Colibri.Core.Settings;
using Colibri.Core.Network;
using Colibri.Core.Platform;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Colibri.App.Services;

/// <summary>
/// Keeps settings in JSON. Missing files use defaults; unreadable existing files block startup
/// so a stored network policy cannot silently become an unrestricted route.
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
    private readonly ICredentialProtector? _credentialProtector;
    private static readonly Guid NetworkSettingsId = new("3f8adb19-26f5-4a73-b878-1132d252ac42");

    public JsonSettingsStore(string path, ILogger<JsonSettingsStore> logger, ICredentialProtector? credentialProtector = null)
    {
        _path = path;
        _logger = logger;
        _credentialProtector = credentialProtector;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, ct)
                ?? throw new CredentialProtectionException();
            if (settings.ProtectedDefaultNetworkPolicy is { } protectedPolicy)
            {
                byte[] ciphertext;
                try { ciphertext = Convert.FromBase64String(protectedPolicy); }
                catch (FormatException) { throw new CredentialProtectionException(); }
                settings.DefaultNetworkPolicy = NetworkCredentialStorage.Unprotect(ciphertext, NetworkSettingsId, _credentialProtector);
            }
            return Normalize(settings);
        }
        catch (FileNotFoundException) { return new AppSettings(); }
        catch (DirectoryNotFoundException) { return new AppSettings(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning("Could not safely read the settings file {Path}; network operations are blocked", _path);
            throw new CredentialProtectionException();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            if (settings.DefaultNetworkPolicy is { } network)
            {
                settings.ProtectedDefaultNetworkPolicy = Convert.ToBase64String(
                    NetworkCredentialStorage.Protect(network, NetworkSettingsId, _credentialProtector));
            }
            else settings.ProtectedDefaultNetworkPolicy = null;
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
        settings.BackgroundPalette = BackgroundPalettes.Normalize(settings.BackgroundPalette);
        settings.Language = LanguagePreference.Normalize(settings.Language);
        settings.CategoryFolders ??= defaults.CategoryFolders;
        settings.BrowserCaptureExtensions ??= defaults.BrowserCaptureExtensions;
        settings.BrowserExclusionRules ??= [];
        settings.BrowserExcludedSites ??= [];
        settings.BrowserCapturePolicy = Colibri.Core.Services.BrowserCaptureRules.Normalize(settings.BrowserCapturePolicy ?? Colibri.Core.Services.BrowserCaptureRules.Migrate(settings.BrowserCaptureExtensions));
        settings.BrowserExclusionRules = settings.BrowserExclusionRules.Where(r => Colibri.Core.Services.BrowserCaptureRules.TryNormalizeRule(r, out _)).Take(256).Distinct().ToList();
        settings.DefaultDownloadFolder ??= defaults.DefaultDownloadFolder;
        settings.Aria2Path ??= defaults.Aria2Path;
        settings.Layout ??= new WindowLayout();
        settings.Layout.Normalize();
        return settings;
    }
}

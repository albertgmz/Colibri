using System.Runtime.Versioning;
using Colibri.Core.Platform;
using Microsoft.Win32;

namespace Colibri.Platform.BrowserHost;

/// <summary>
/// Windows: the manifest is written to <c>&lt;data folder&gt;\NativeMessagingHosts\&lt;name&gt;.json</c>, and
/// each browser finds it through the default value of a per-user registry key, which holds the manifest's
/// absolute path: <c>HKCU\Software\Google\Chrome\NativeMessagingHosts\&lt;name&gt;</c> and
/// <c>HKCU\Software\Microsoft\Edge\NativeMessagingHosts\&lt;name&gt;</c> (Edge also reads Chrome's key, but
/// prefers its own). Both keys are always written, whether or not the browser is installed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBrowserHostRegistrar : IBrowserHostRegistrar
{
    private static readonly (BrowserKind Browser, string KeyPath)[] Browsers =
    [
        (BrowserKind.Chrome, @"Google\Chrome\NativeMessagingHosts"),
        (BrowserKind.Edge, @"Microsoft\Edge\NativeMessagingHosts"),
        (BrowserKind.Firefox, @"Mozilla\NativeMessagingHosts"),
    ];

    private readonly string _manifestFolder;
    private readonly string _softwareKeyPath;

    public WindowsBrowserHostRegistrar(IAppPaths paths)
        : this(Path.Combine(paths.DataDirectory, "NativeMessagingHosts"), "Software")
    {
    }

    /// <summary>For tests: another manifest folder, and another key under HKEY_CURRENT_USER instead of "Software".</summary>
    internal WindowsBrowserHostRegistrar(string manifestFolder, string softwareKeyPath)
    {
        _manifestFolder = manifestFolder;
        _softwareKeyPath = softwareKeyPath;
    }

    public async Task<IReadOnlyList<BrowserHostStatus>> GetStatusAsync(NativeHostRegistration expected)
    {
        var manifestPath = ManifestPath(expected);
        // A host file that is gone (Colibri deleted or moved) cannot be started: the registration needs a repair.
        var manifestMatches = File.Exists(manifestPath)
            && File.Exists(expected.HostExecutablePath)
            && NativeHostManifest.Matches(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false), expected, StringComparer.OrdinalIgnoreCase);
        var firefoxPath = FirefoxManifestPath(expected);
        var firefoxMatches = File.Exists(firefoxPath) && File.Exists(expected.HostExecutablePath)
            && NativeHostManifest.Matches(await File.ReadAllTextAsync(firefoxPath).ConfigureAwait(false), expected, StringComparer.OrdinalIgnoreCase, true);

        return Browsers.Select(b =>
        {
            var matches = b.Browser == BrowserKind.Firefox ? firefoxMatches : manifestMatches;
            var expectedPath = b.Browser == BrowserKind.Firefox ? firefoxPath : manifestPath;
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath(b.KeyPath, expected));
            var status = key?.GetValue(null) switch
            {
                null => BrowserIntegrationStatus.NotRegistered,
                string registered when matches && string.Equals(registered, expectedPath, StringComparison.OrdinalIgnoreCase)
                    => BrowserIntegrationStatus.Registered,
                _ => BrowserIntegrationStatus.Outdated,
            };
            return new BrowserHostStatus(b.Browser, status);
        }).ToList();
    }

    public async Task RegisterAsync(NativeHostRegistration registration)
    {
        NativeHostManifest.EnsureHostExists(registration);
        Directory.CreateDirectory(_manifestFolder);
        var manifestPath = ManifestPath(registration);
        await File.WriteAllTextAsync(manifestPath, NativeHostManifest.Build(registration)).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(FirefoxManifestPath(registration))!);
        await File.WriteAllTextAsync(FirefoxManifestPath(registration), NativeHostManifest.Build(registration, true)).ConfigureAwait(false);

        foreach (var (browser, keyPath) in Browsers)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath(keyPath, registration));
            key.SetValue(null, browser == BrowserKind.Firefox ? FirefoxManifestPath(registration) : manifestPath, RegistryValueKind.String);
        }
    }

    private string ManifestPath(NativeHostRegistration registration) =>
        Path.Combine(_manifestFolder, NativeHostManifest.FileName(registration));

    private string FirefoxManifestPath(NativeHostRegistration registration) =>
        Path.Combine(_manifestFolder, "firefox", NativeHostManifest.FileName(registration));

    private string KeyPath(string browserKeyPath, NativeHostRegistration registration) =>
        $@"{_softwareKeyPath}\{browserKeyPath}\{registration.HostName}";
}

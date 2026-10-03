using Colibri.Core.Platform;

namespace Colibri.Platform.BrowserHost;

/// <summary>A browser's per-user profile folder; its <c>NativeMessagingHosts</c> subfolder holds host manifests.</summary>
internal sealed record BrowserProfileFolder(BrowserKind Browser, string ProfileFolder)
{
    public string HostsFolder => Path.Combine(ProfileFolder, "NativeMessagingHosts");
}

/// <summary>
/// Linux and macOS: the manifest is a file <c>&lt;name&gt;.json</c> in the browser's per-user
/// <c>NativeMessagingHosts</c> folder; there is no registry. Only browsers whose profile folder exists (the
/// browser has been run by this user) are listed and set up; the <c>NativeMessagingHosts</c> folder is created
/// when missing.
/// </summary>
internal sealed class UnixBrowserHostRegistrar : IBrowserHostRegistrar
{
    private readonly IReadOnlyList<BrowserProfileFolder> _browsers;

    public UnixBrowserHostRegistrar(IReadOnlyList<BrowserProfileFolder> browsers)
    {
        _browsers = browsers;
    }

    /// <summary>
    /// Linux: Chrome, Chromium and Edge keep their profiles in <c>$XDG_CONFIG_HOME</c> (by default
    /// <c>~/.config</c>; a relative value is ignored, as the XDG spec requires).
    /// </summary>
    public static IReadOnlyList<BrowserProfileFolder> LinuxFolders(string home, string? xdgConfigHome)
    {
        var config = !string.IsNullOrEmpty(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome) ? xdgConfigHome : Path.Combine(home, ".config");
        return
        [
            new(BrowserKind.Chrome, Path.Combine(config, "google-chrome")),
            new(BrowserKind.Chromium, Path.Combine(config, "chromium")),
            new(BrowserKind.Edge, Path.Combine(config, "microsoft-edge")),
        ];
    }

    /// <summary>macOS: the profiles live in <c>~/Library/Application Support</c>.</summary>
    public static IReadOnlyList<BrowserProfileFolder> MacFolders(string home)
    {
        var support = Path.Combine(home, "Library", "Application Support");
        return
        [
            new(BrowserKind.Chrome, Path.Combine(support, "Google", "Chrome")),
            new(BrowserKind.Chromium, Path.Combine(support, "Chromium")),
            new(BrowserKind.Edge, Path.Combine(support, "Microsoft Edge")),
        ];
    }

    public async Task<IReadOnlyList<BrowserHostStatus>> GetStatusAsync(NativeHostRegistration expected)
    {
        var statuses = new List<BrowserHostStatus>();
        foreach (var browser in InstalledBrowsers())
        {
            var manifestPath = Path.Combine(browser.HostsFolder, NativeHostManifest.FileName(expected));
            var status = !File.Exists(manifestPath)
                ? BrowserIntegrationStatus.NotRegistered
                : NativeHostManifest.Matches(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false), expected, StringComparer.Ordinal)
                    ? BrowserIntegrationStatus.Registered
                    : BrowserIntegrationStatus.Outdated;
            statuses.Add(new BrowserHostStatus(browser.Browser, status));
        }

        return statuses;
    }

    public async Task RegisterAsync(NativeHostRegistration registration)
    {
        NativeHostManifest.EnsureHostExists(registration);

        // The browser starts the host directly, so it must be executable. A copy that lost its execute bit
        // (for example unpacked from a zip file) gets it back for its owner. Only then: a host installed by
        // another user (root in /opt) may not be changed, and is already executable.
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(registration.HostExecutablePath);
            if (!mode.HasFlag(UnixFileMode.UserExecute))
            {
                File.SetUnixFileMode(registration.HostExecutablePath, mode | UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
        }

        var manifest = NativeHostManifest.Build(registration);
        foreach (var browser in InstalledBrowsers())
        {
            Directory.CreateDirectory(browser.HostsFolder);
            await File.WriteAllTextAsync(Path.Combine(browser.HostsFolder, NativeHostManifest.FileName(registration)), manifest).ConfigureAwait(false);
        }
    }

    private IEnumerable<BrowserProfileFolder> InstalledBrowsers() => _browsers.Where(b => Directory.Exists(b.ProfileFolder));
}

using System.Runtime.Versioning;
using System.Text.Json;
using Colibri.Core.Platform;
using Colibri.Platform.BrowserHost;
using Microsoft.Win32;

namespace Colibri.Platform.Tests;

public sealed class BrowserHostRegistrarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "colibri-browserhost-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static NativeHostRegistration Registration(string hostPath, params string[] origins) => new()
    {
        Description = "Colibri download manager",
        HostExecutablePath = hostPath,
        AllowedOrigins = origins.Length > 0 ? origins : [BrowserExtension.AllowedOrigin],
    };

    // ---- Manifest ----

    [Fact]
    public void Manifest_has_the_fields_chrome_expects()
    {
        var json = NativeHostManifest.Build(Registration(@"C:\Program Files\Colibri\Colibri.NativeHost.exe"));

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("com.colibri.host", root.GetProperty("name").GetString());
        Assert.Equal("Colibri download manager", root.GetProperty("description").GetString());
        Assert.Equal(@"C:\Program Files\Colibri\Colibri.NativeHost.exe", root.GetProperty("path").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.Equal(["chrome-extension://lelenggmjjaffoebgecdpemmjakhofni/"], root.GetProperty("allowed_origins").EnumerateArray().Select(o => o.GetString()));
    }

    [Fact]
    public void Manifest_matching_compares_name_path_and_origins()
    {
        var expected = Registration("/opt/colibri/Colibri.NativeHost");
        var json = NativeHostManifest.Build(expected);

        Assert.True(NativeHostManifest.Matches(json, expected with { Description = "other text" }, StringComparer.Ordinal));
        Assert.False(NativeHostManifest.Matches(json, expected with { HostExecutablePath = "/elsewhere/Colibri.NativeHost" }, StringComparer.Ordinal));
        Assert.False(NativeHostManifest.Matches(json, expected with { HostExecutablePath = "/OPT/colibri/Colibri.NativeHost" }, StringComparer.Ordinal));
        Assert.True(NativeHostManifest.Matches(json, expected with { HostExecutablePath = "/OPT/colibri/Colibri.NativeHost" }, StringComparer.OrdinalIgnoreCase));
        Assert.False(NativeHostManifest.Matches(json, expected with { AllowedOrigins = ["chrome-extension://abcdefghijklmnopabcdefghijklmnop/"] }, StringComparer.Ordinal));
        Assert.False(NativeHostManifest.Matches(json, expected with { HostName = "com.other.host" }, StringComparer.Ordinal));
        Assert.False(NativeHostManifest.Matches("{ not json", expected, StringComparer.Ordinal));
        Assert.False(NativeHostManifest.Matches("""{"name":"com.colibri.host"}""", expected, StringComparer.Ordinal));
    }

    // ---- Linux and macOS folders ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("relative", null)]
    [InlineData("/data/config", "/data/config")]
    public void Linux_profile_folders_follow_xdg_config_home(string? xdgConfigHome, string? expectedConfig)
    {
        // null: the default, ~/.config
        var config = expectedConfig ?? Path.Combine("/home/ana", ".config");
        var folders = UnixBrowserHostRegistrar.LinuxFolders("/home/ana", xdgConfigHome);

        Assert.Equal(
            [
                (BrowserKind.Chrome, Path.Combine(config, "google-chrome", "NativeMessagingHosts")),
                (BrowserKind.Chromium, Path.Combine(config, "chromium", "NativeMessagingHosts")),
                (BrowserKind.Edge, Path.Combine(config, "microsoft-edge", "NativeMessagingHosts")),
            ],
            folders.Select(f => (f.Browser, f.HostsFolder)));
    }

    [Fact]
    public void Mac_profile_folders_are_in_application_support()
    {
        var support = Path.Combine("/Users/ana", "Library", "Application Support");

        Assert.Equal(
            [
                (BrowserKind.Chrome, Path.Combine(support, "Google", "Chrome", "NativeMessagingHosts")),
                (BrowserKind.Chromium, Path.Combine(support, "Chromium", "NativeMessagingHosts")),
                (BrowserKind.Edge, Path.Combine(support, "Microsoft Edge", "NativeMessagingHosts")),
            ],
            UnixBrowserHostRegistrar.MacFolders("/Users/ana").Select(f => (f.Browser, f.HostsFolder)));
    }

    [Fact]
    public async Task Unix_registration_round_trip_in_a_temp_home()
    {
        var home = Path.Combine(_root, "home");
        var folders = UnixBrowserHostRegistrar.LinuxFolders(home, null);
        Directory.CreateDirectory(folders[0].ProfileFolder); // Chrome has been run; Chromium and Edge have not.
        Directory.CreateDirectory(folders[2].ProfileFolder); // Edge too.
        var host = Path.Combine(_root, "app", "Colibri.NativeHost");
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        await File.WriteAllTextAsync(host, "#!/bin/sh\n", TestContext.Current.CancellationToken);
        var registrar = new UnixBrowserHostRegistrar(folders);
        var expected = Registration(host);

        Assert.Equal(
            [new(BrowserKind.Chrome, BrowserIntegrationStatus.NotRegistered), new(BrowserKind.Edge, BrowserIntegrationStatus.NotRegistered)],
            await registrar.GetStatusAsync(expected));

        await registrar.RegisterAsync(expected);

        Assert.Equal(
            [new(BrowserKind.Chrome, BrowserIntegrationStatus.Registered), new(BrowserKind.Edge, BrowserIntegrationStatus.Registered)],
            await registrar.GetStatusAsync(expected));
        Assert.True(File.Exists(Path.Combine(folders[0].HostsFolder, "com.colibri.host.json")));
        Assert.False(Directory.Exists(folders[1].ProfileFolder)); // Chromium was not created.
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(host).HasFlag(UnixFileMode.UserExecute));
        }

        // A moved app makes the registration outdated until it is repaired.
        var moved = expected with { HostExecutablePath = CreateHost("moved", "Colibri.NativeHost") };
        Assert.All(await registrar.GetStatusAsync(moved), s => Assert.Equal(BrowserIntegrationStatus.Outdated, s.Status));
        await registrar.RegisterAsync(moved);
        Assert.All(await registrar.GetStatusAsync(moved), s => Assert.Equal(BrowserIntegrationStatus.Registered, s.Status));
    }

    [Fact]
    public async Task Registering_a_missing_host_fails_and_writes_nothing()
    {
        var folders = UnixBrowserHostRegistrar.LinuxFolders(Path.Combine(_root, "home"), null);
        Directory.CreateDirectory(folders[0].ProfileFolder);
        var registrar = new UnixBrowserHostRegistrar(folders);

        await Assert.ThrowsAsync<FileNotFoundException>(() => registrar.RegisterAsync(Registration(Path.Combine(_root, "missing", "Colibri.NativeHost"))));

        Assert.False(Directory.Exists(folders[0].HostsFolder));
    }

    private string CreateHost(string folder, string fileName)
    {
        var path = Path.Combine(_root, folder, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "host");
        return path;
    }

    // ---- Windows registry ----

    [Fact]
    [SupportedOSPlatform("windows")] // the skip below guards the Windows-only registry calls
    public async Task Windows_registration_round_trip_in_a_test_key()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry");

        // A throwaway key under HKCU, never the real browser keys.
        var software = @"Software\Colibri.Tests\" + Guid.NewGuid().ToString("N");
        var manifestFolder = Path.Combine(_root, "NativeMessagingHosts");
        var registrar = new WindowsBrowserHostRegistrar(manifestFolder, software);
        var expected = Registration(CreateHost("Apps", "Colibri.NativeHost.exe"));
        var manifestPath = Path.Combine(manifestFolder, "com.colibri.host.json");
        try
        {
            Assert.Equal(
                [new(BrowserKind.Chrome, BrowserIntegrationStatus.NotRegistered), new(BrowserKind.Edge, BrowserIntegrationStatus.NotRegistered)],
                await registrar.GetStatusAsync(expected));

            await registrar.RegisterAsync(expected);

            foreach (var key in (string[])[@"Google\Chrome\NativeMessagingHosts\com.colibri.host", @"Microsoft\Edge\NativeMessagingHosts\com.colibri.host"])
            {
                using var opened = Registry.CurrentUser.OpenSubKey($@"{software}\{key}");
                Assert.Equal(manifestPath, opened!.GetValue(null));
            }

            Assert.True(NativeHostManifest.Matches(await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken), expected, StringComparer.Ordinal));
            Assert.All(await registrar.GetStatusAsync(expected), s => Assert.Equal(BrowserIntegrationStatus.Registered, s.Status));
            Assert.All(
                await registrar.GetStatusAsync(expected with { HostExecutablePath = expected.HostExecutablePath.ToUpperInvariant() }),
                s => Assert.Equal(BrowserIntegrationStatus.Registered, s.Status));

            // Another copy of Colibri, or a key pointing at another manifest, needs a repair.
            Assert.All(
                await registrar.GetStatusAsync(expected with { HostExecutablePath = @"D:\Other\Colibri.NativeHost.exe" }),
                s => Assert.Equal(BrowserIntegrationStatus.Outdated, s.Status));
            using (var edge = Registry.CurrentUser.CreateSubKey($@"{software}\Microsoft\Edge\NativeMessagingHosts\com.colibri.host"))
            {
                edge.SetValue(null, @"C:\elsewhere\com.colibri.host.json");
            }

            Assert.Equal(
                [new(BrowserKind.Chrome, BrowserIntegrationStatus.Registered), new(BrowserKind.Edge, BrowserIntegrationStatus.Outdated)],
                await registrar.GetStatusAsync(expected));

            // A deleted manifest file also needs a repair.
            File.Delete(manifestPath);
            Assert.All(await registrar.GetStatusAsync(expected), s => Assert.Equal(BrowserIntegrationStatus.Outdated, s.Status));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Colibri.Tests", throwOnMissingSubKey: false);
        }
    }
}

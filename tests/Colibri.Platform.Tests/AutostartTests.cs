using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;
using Colibri.Platform.Autostart;
using Microsoft.Win32;

namespace Colibri.Platform.Tests;

public sealed class AutostartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "colibri-autostart-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- Windows Run key ----

    [Fact]
    public void Run_value_quotes_the_exe_and_passes_minimized()
    {
        Assert.Equal(
            "\"C:\\Program Files\\Colibri\\Colibri.exe\" --minimized",
            RunKeyCommand.Build(@"C:\Program Files\Colibri\Colibri.exe"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Colibri\\Colibri.exe\" --minimized", @"C:\Program Files\Colibri\Colibri.exe")]
    [InlineData("  \"C:\\Apps\\Colibri.exe\"", @"C:\Apps\Colibri.exe")]
    [InlineData(@"C:\Apps\Colibri.exe --minimized", @"C:\Apps\Colibri.exe")]
    [InlineData(@"C:\Apps\Colibri.exe", @"C:\Apps\Colibri.exe")]
    [InlineData("\"C:\\Apps\\Colibri.exe", @"C:\Apps\Colibri.exe")]
    public void Run_value_executable_is_read_back(string command, string expected)
    {
        Assert.Equal(expected, RunKeyCommand.ExecutableOf(command));
    }

    [Fact]
    [SupportedOSPlatform("windows")] // the skip below guards the Windows-only registry calls
    public async Task Windows_autostart_round_trip_in_a_test_key()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry");

        // A throwaway key under HKCU, never the real Run key.
        var keyPath = @"Software\Colibri.Tests\" + Guid.NewGuid().ToString("N");
        var exe = @"C:\Program Files\Colibri\Colibri.exe";
        var service = new WindowsAutostartService(keyPath, exe);
        try
        {
            Assert.False(await service.IsEnabledAsync());

            await service.SetEnabledAsync(true);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Equal("\"C:\\Program Files\\Colibri\\Colibri.exe\" --minimized", key!.GetValue("Colibri"));
            }

            Assert.True(await service.IsEnabledAsync());
            Assert.True(await new WindowsAutostartService(keyPath, exe.ToUpperInvariant()).IsEnabledAsync());
            Assert.False(await new WindowsAutostartService(keyPath, @"D:\Other\Colibri.exe").IsEnabledAsync());

            await service.SetEnabledAsync(false);
            await service.SetEnabledAsync(false);
            Assert.False(await service.IsEnabledAsync());
        }
        finally
        {
            // Delete only this test's own key: tests run in parallel, and removing the shared
            // Software\Colibri.Tests parent would pull the key out from under another test.
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    // ---- Linux desktop entry ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("relative/config", null)]
    [InlineData("/data/config", "/data/config")]
    public void Xdg_autostart_folder_comes_from_xdg_config_home(string? xdgConfigHome, string? expectedConfigHome)
    {
        // null: the default, ~/.config
        Assert.Equal(
            Path.Combine(expectedConfigHome ?? Path.Combine("/home/ana", ".config"), "autostart"),
            LinuxAutostartService.ResolveAutostartDirectory(xdgConfigHome, "/home/ana"));
    }

    [Theory]
    [InlineData("/home/ana/Colibri.AppImage", "/tmp/.mount_ColibrX", "/tmp/.mount_ColibrX/usr/bin/Colibri", "/home/ana/Colibri.AppImage")]
    [InlineData("/home/ana/Terminal.AppImage", "/tmp/.mount_TermY", "/opt/colibri/Colibri", "/opt/colibri/Colibri")]
    [InlineData(null, null, "/opt/colibri/Colibri", "/opt/colibri/Colibri")]
    [InlineData("/home/ana/Colibri.AppImage", null, "/tmp/.mount_ColibrX/usr/bin/Colibri", "/tmp/.mount_ColibrX/usr/bin/Colibri")]
    public void AppImage_is_launched_only_when_colibri_runs_inside_it(string? appImage, string? appDir, string executable, string expected)
    {
        Assert.Equal(expected, LinuxAutostartService.LaunchTarget(appImage, appDir, executable));
    }

    [Theory]
    [InlineData("/opt/colibri/Colibri", "/opt/colibri/Colibri")]
    [InlineData("/home/ana/My Apps/Colibri", "\"/home/ana/My Apps/Colibri\"")]
    [InlineData("/apps/100%/Colibri", "/apps/100%%/Colibri")]
    [InlineData("/apps/a\"b$c`d/Colibri", "\"/apps/a\\\"b\\$c\\`d/Colibri\"")]
    [InlineData(@"/apps/back\slash", "\"/apps/back\\\\slash\"")]
    [InlineData("/apps/it's/Colibri", "\"/apps/it's/Colibri\"")]
    public void Exec_arguments_are_quoted_per_the_desktop_entry_spec(string argument, string expected)
    {
        Assert.Equal(expected, LinuxAutostartService.QuoteExecArgument(argument));
    }

    [Fact]
    public void Desktop_entry_has_the_autostart_keys()
    {
        var entry = LinuxAutostartService.BuildDesktopEntry("/home/ana/My Apps/Colibri.AppImage");

        Assert.Equal(
            "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=Colibri\n"
            + "Exec=\"/home/ana/My Apps/Colibri.AppImage\" --minimized\n"
            + "Terminal=false\n"
            + "X-GNOME-Autostart-enabled=true\n",
            entry);
    }

    [Fact]
    public void Desktop_entry_string_escaping_doubles_the_quoting_backslashes()
    {
        // Spec: a literal backslash inside a quoted argument is written as four backslashes.
        var entry = LinuxAutostartService.BuildDesktopEntry(@"/apps/back\slash");

        Assert.Contains("Exec=\"/apps/back\\\\\\\\slash\" --minimized\n", entry);
    }

    [Fact]
    public async Task Linux_autostart_round_trip_in_a_temp_folder()
    {
        var folder = Path.Combine(_root, "config", "autostart");
        var service = new LinuxAutostartService(folder, "/opt/colibri/Colibri");

        Assert.False(await service.IsEnabledAsync());

        await service.SetEnabledAsync(true);
        Assert.True(File.Exists(Path.Combine(folder, "colibri.desktop")));
        Assert.True(await service.IsEnabledAsync());
        Assert.False(await new LinuxAutostartService(folder, "/elsewhere/Colibri").IsEnabledAsync());

        await service.SetEnabledAsync(false);
        await service.SetEnabledAsync(false);
        Assert.False(File.Exists(Path.Combine(folder, "colibri.desktop")));
        Assert.False(await service.IsEnabledAsync());
    }

    // ---- macOS LaunchAgent ----

    [Fact]
    public void Launch_agent_plist_is_valid_xml_with_the_program_arguments()
    {
        var exe = "/Applications/Colibri & Co <beta>.app/Contents/MacOS/Colibri";

        var plist = ParsePlist(MacAutostartService.BuildLaunchAgentPlist(exe));

        var dict = plist.Root!.Element("dict")!;
        var values = dict.Elements().ToList();
        string ValueAfter(string key) => values[values.FindIndex(e => e.Name == "key" && e.Value == key) + 1].ToString();

        Assert.Equal("<string>com.colibri.app</string>", ValueAfter("Label"));
        Assert.Equal("<true />", ValueAfter("RunAtLoad"));
        var arguments = values[values.FindIndex(e => e.Value == "ProgramArguments") + 1].Elements("string").Select(e => e.Value);
        Assert.Equal([exe, "--minimized"], arguments);
    }

    [Fact]
    public async Task Mac_autostart_round_trip_in_a_temp_folder()
    {
        var folder = Path.Combine(_root, "LaunchAgents");
        var service = new MacAutostartService(folder, "/Applications/Colibri.app/Contents/MacOS/Colibri");

        await service.SetEnabledAsync(true);
        Assert.True(File.Exists(Path.Combine(folder, "com.colibri.app.plist")));
        Assert.True(await service.IsEnabledAsync());
        Assert.False(await new MacAutostartService(folder, "/elsewhere/Colibri").IsEnabledAsync());

        await service.SetEnabledAsync(false);
        Assert.False(await service.IsEnabledAsync());
    }

    private static XDocument ParsePlist(string xml)
    {
        // The DOCTYPE points at Apple's DTD on the web; it must not be fetched.
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return XDocument.Load(reader);
    }
}

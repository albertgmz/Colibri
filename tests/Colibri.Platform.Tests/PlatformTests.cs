using Colibri.Platform.Aria2;
using Colibri.Platform.Paths;

namespace Colibri.Platform.Tests;

public sealed class PlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "colibri-platform-tests", Guid.NewGuid().ToString("N"));

    public PlatformTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateFile(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    // ---- XDG Downloads folder ----

    [Theory]
    [InlineData("XDG_DOWNLOAD_DIR=\"$HOME/Descargas\"", "/home/ana/Descargas")]
    [InlineData("  XDG_DOWNLOAD_DIR=\"/data/downloads/\"  ", "/data/downloads")]
    [InlineData("XDG_DOWNLOAD_DIR=\"$HOME/\"", null)]
    [InlineData("XDG_DOWNLOAD_DIR=\"relative/dir\"", null)]
    [InlineData("XDG_MUSIC_DIR=\"$HOME/Music\"", null)]
    public void Xdg_download_dir_is_read_from_user_dirs(string line, string? expected)
    {
        string[] lines = ["# This file is written by xdg-user-dirs-update", "XDG_DESKTOP_DIR=\"$HOME/Desktop\"", line];

        Assert.Equal(expected, XdgDownloadsFolderLocator.ParseDownloadDir(lines, "/home/ana"));
    }

    // ---- aria2 locators ----

    [Fact]
    public void Windows_locator_prefers_an_existing_configured_path_then_the_bundled_copy()
    {
        var configured = CreateFile("custom", "aria2c.exe");
        var bundled = CreateFile("app", "aria2", "aria2c.exe");
        var locator = new WindowsAria2Locator(bundled);

        Assert.Equal(configured, locator.FindAria2(configured));
        Assert.Equal(bundled, locator.FindAria2(null));
        Assert.Equal(bundled, locator.FindAria2(Path.Combine(_root, "missing.exe")));
    }

    [Fact]
    public void Windows_locator_returns_null_when_nothing_exists()
    {
        var locator = new WindowsAria2Locator(Path.Combine(_root, "aria2", "aria2c.exe"));

        Assert.Null(locator.FindAria2(null));
    }

    [Fact]
    public void Unix_locator_searches_absolute_path_entries_then_the_extra_folders()
    {
        var onPath = CreateFile("bin", "aria2c");
        var extra = CreateFile("homebrew", "aria2c");
        var missing = Path.Combine(_root, "nonexistent");
        var pathVariable = missing + Path.PathSeparator + Path.GetDirectoryName(onPath);

        var withPath = new UnixAria2Locator(() => pathVariable, [Path.GetDirectoryName(extra)!]);
        var withoutPath = new UnixAria2Locator(() => missing, [Path.GetDirectoryName(extra)!]);

        Assert.Equal(onPath, withPath.FindAria2(null));
        Assert.Equal(extra, withoutPath.FindAria2(null));
    }

    [Fact]
    public void Unix_locator_never_uses_relative_path_entries()
    {
        // "." and "bin" are relative: they would mean "the current directory" and must be skipped even
        // when an aria2c exists there.
        var previous = Environment.CurrentDirectory;
        CreateFile("aria2c");
        CreateFile("bin", "aria2c");
        Environment.CurrentDirectory = _root;
        try
        {
            var sep = Path.PathSeparator;
            var locator = new UnixAria2Locator(() => $".{sep}bin{sep}{sep}", []);

            Assert.Null(locator.FindAria2(null));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void Unix_locator_uses_an_existing_configured_path()
    {
        var configured = CreateFile("opt", "aria2c");

        Assert.Equal(configured, new UnixAria2Locator(() => null, []).FindAria2(configured));
    }

    // ---- App paths ----

    [Fact]
    public void App_paths_live_in_the_data_folder_and_create_it()
    {
        var data = Path.Combine(_root, "Colibri");

        var paths = new AppPaths(data, new FixedDownloads("/downloads"));

        Assert.True(Directory.Exists(data));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.Equal(Path.Combine(data, "colibri.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(data, "settings.json"), paths.SettingsPath);
        Assert.Equal(Path.Combine(data, "aria2.session"), paths.Aria2SessionPath);
        Assert.Equal("/downloads", paths.DefaultDownloadsDirectory);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(data));
        }
    }

    [Fact]
    public void An_existing_data_folder_is_tightened_to_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Path.Combine(_root, "Existing");
        Directory.CreateDirectory(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _ = new AppPaths(data, new FixedDownloads("/downloads"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(data));
    }

    [Fact]
    public void Relative_configured_aria2_paths_are_ignored()
    {
        var previous = Environment.CurrentDirectory;
        CreateFile("aria2c.exe");
        CreateFile("aria2c");
        Environment.CurrentDirectory = _root;
        try
        {
            Assert.Null(new WindowsAria2Locator(Path.Combine(_root, "missing", "aria2c.exe")).FindAria2("aria2c.exe"));
            Assert.Null(new UnixAria2Locator(() => null, []).FindAria2("aria2c"));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void Windows_downloads_folder_is_an_existing_absolute_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = new WindowsDownloadsFolderLocator().GetDownloadsFolder();

        Assert.True(Path.IsPathFullyQualified(folder));
    }

    private sealed class FixedDownloads(string folder) : IDownloadsFolderLocator
    {
        public string GetDownloadsFolder() => folder;
    }
}

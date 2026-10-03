using System.Text;
using Colibri.Core.Platform;

namespace Colibri.Platform.Autostart;

/// <summary>
/// Linux: a <c>colibri.desktop</c> file in <c>$XDG_CONFIG_HOME/autostart</c> (normally
/// <c>~/.config/autostart</c>), which desktops following the XDG Autostart spec start at login. When Colibri
/// runs from an AppImage, the AppImage file ($APPIMAGE) is started, not the program inside its temporary
/// mount. Autostart counts as enabled only while the file holds exactly what this service writes, so an
/// entry for a moved copy, or one the desktop's settings switched off, reads as disabled.
/// </summary>
internal sealed class LinuxAutostartService : IAutostartService
{
    private readonly string _filePath;
    private readonly string _executablePath;

    public LinuxAutostartService()
        : this(
            ResolveAutostartDirectory(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            LaunchTarget(Environment.GetEnvironmentVariable("APPIMAGE"), Environment.GetEnvironmentVariable("APPDIR"), AutostartTarget.ExecutablePath()))
    {
    }

    internal LinuxAutostartService(string autostartDirectory, string executablePath)
    {
        _filePath = Path.Combine(autostartDirectory, "colibri.desktop");
        _executablePath = executablePath;
    }

    public bool IsSupported => true;

    public async Task<bool> IsEnabledAsync() =>
        File.Exists(_filePath) && await File.ReadAllTextAsync(_filePath).ConfigureAwait(false) == BuildDesktopEntry(_executablePath);

    public async Task SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.WriteAllTextAsync(_filePath, BuildDesktopEntry(_executablePath)).ConfigureAwait(false);
        }
        else
        {
            File.Delete(_filePath); // no error when it does not exist
        }
    }

    /// <summary>
    /// The AppImage runtime sets APPIMAGE (the .AppImage file) and APPDIR (its temporary mount) for the
    /// program inside. Both are inherited by child processes, so they only count when this executable
    /// really runs from that mount; otherwise Colibri was merely started from another AppImage.
    /// </summary>
    internal static string LaunchTarget(string? appImage, string? appDir, string executablePath) =>
        !string.IsNullOrEmpty(appImage) && !string.IsNullOrEmpty(appDir)
            && executablePath.StartsWith(appDir.TrimEnd('/') + "/", StringComparison.Ordinal)
            ? appImage
            : executablePath;

    /// <summary>
    /// $XDG_CONFIG_HOME/autostart, or ~/.config/autostart when the variable is unset, empty or relative
    /// (the XDG Base Directory spec says relative values must be ignored).
    /// </summary>
    internal static string ResolveAutostartDirectory(string? xdgConfigHome, string home)
    {
        var configHome = !string.IsNullOrEmpty(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome)
            ? xdgConfigHome
            : Path.Combine(home, ".config");
        return Path.Combine(configHome, "autostart");
    }

    internal static string BuildDesktopEntry(string executablePath)
    {
        var exec = EscapeStringValue($"{QuoteExecArgument(executablePath)} {AutostartTarget.MinimizedArgument}");
        return "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=Colibri\n"
            + $"Exec={exec}\n"
            + "Terminal=false\n"
            + "X-GNOME-Autostart-enabled=true\n";
    }

    /// <summary>
    /// One argument of an Exec line, per the Desktop Entry spec: arguments containing reserved characters
    /// are put in double quotes, inside which <c>" ` $ \</c> are backslash-escaped. A literal '%' is written
    /// "%%" everywhere, because '%' starts field codes such as %f.
    /// </summary>
    internal static string QuoteExecArgument(string argument)
    {
        const string reserved = " \t\n\"'\\><~|&;$*?#()`";
        var quoted = argument.IndexOfAny(reserved.ToCharArray()) >= 0;
        var builder = new StringBuilder();
        if (quoted)
        {
            builder.Append('"');
        }

        foreach (var c in argument)
        {
            if (quoted && c is '"' or '`' or '$' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c == '%' ? "%%" : c.ToString());
        }

        if (quoted)
        {
            builder.Append('"');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes for a desktop entry string value. Readers undo this before the Exec quoting above, which is
    /// why a literal backslash in a quoted argument ends up as four backslashes in the file.
    /// </summary>
    private static string EscapeStringValue(string value) =>
        value.Replace("\\", @"\\").Replace("\n", @"\n").Replace("\t", @"\t").Replace("\r", @"\r");
}

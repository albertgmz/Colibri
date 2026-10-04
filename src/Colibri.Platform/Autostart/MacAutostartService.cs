using System.Security;
using Colibri.Core.Platform;

namespace Colibri.Platform.Autostart;

/// <summary>
/// macOS: a launchd agent, <c>~/Library/LaunchAgents/com.colibri.app.plist</c>, with RunAtLoad so launchd
/// starts Colibri at the next login. launchctl is not called: the file alone takes effect from the next
/// login, and loading it now would start a second Colibri. Autostart counts as enabled only while the file
/// holds exactly what this service writes.
/// </summary>
internal sealed class MacAutostartService : IAutostartService
{
    private const string Label = "com.colibri.app";

    private readonly string _filePath;
    private readonly string _executablePath;

    public MacAutostartService()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
            AutostartTarget.ExecutablePath())
    {
    }

    internal MacAutostartService(string launchAgentsDirectory, string executablePath)
    {
        _filePath = Path.Combine(launchAgentsDirectory, Label + ".plist");
        _executablePath = executablePath;
    }

    public bool IsSupported => true;

    public async Task<bool> IsEnabledAsync() =>
        File.Exists(_filePath) && await File.ReadAllTextAsync(_filePath).ConfigureAwait(false) == BuildLaunchAgentPlist(_executablePath);

    public async Task SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.WriteAllTextAsync(_filePath, BuildLaunchAgentPlist(_executablePath)).ConfigureAwait(false);
        }
        else
        {
            File.Delete(_filePath); // no error when it does not exist
        }
    }

    /// <summary>The property list launchd reads; ProgramArguments is run as-is, without a shell.</summary>
    internal static string BuildLaunchAgentPlist(string executablePath) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Label}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(executablePath)}</string>
                <string>{AutostartTarget.MinimizedArgument}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
        </dict>
        </plist>

        """.ReplaceLineEndings("\n");
}

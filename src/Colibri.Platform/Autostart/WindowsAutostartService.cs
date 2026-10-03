using System.Runtime.Versioning;
using Colibri.Core.Platform;
using Microsoft.Win32;

namespace Colibri.Platform.Autostart;

/// <summary>
/// Windows: a value named "Colibri" under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, the
/// per-user list of programs Windows starts at sign-in; the value is the command line built by
/// <see cref="RunKeyCommand"/>. Users can also switch the entry off in Task Manager's Startup tab; Windows
/// records that elsewhere (StartupApproved) and the value stays, so this service still reports it as enabled.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAutostartService : IAutostartService
{
    private const string ValueName = "Colibri";

    private readonly string _runKeyPath;
    private readonly string _executablePath;

    public WindowsAutostartService()
        : this(@"Software\Microsoft\Windows\CurrentVersion\Run", AutostartTarget.ExecutablePath())
    {
    }

    /// <summary>For tests: another key under HKEY_CURRENT_USER instead of the real Run key.</summary>
    internal WindowsAutostartService(string runKeyPath, string executablePath)
    {
        _runKeyPath = runKeyPath;
        _executablePath = executablePath;
    }

    public bool IsSupported => true;

    /// <summary>True when the value exists and starts this exe (a copy elsewhere does not count).</summary>
    public Task<bool> IsEnabledAsync()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        var enabled = key?.GetValue(ValueName) is string command
            && string.Equals(RunKeyCommand.ExecutableOf(command), _executablePath, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(enabled);
    }

    public Task SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath);
            key.SetValue(ValueName, RunKeyCommand.Build(_executablePath));
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }
}

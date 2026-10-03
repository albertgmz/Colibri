namespace Colibri.Core.Platform;

/// <summary>
/// Per-user locations Colibri reads and writes.
/// </summary>
public interface IAppPaths
{
    /// <summary>Root folder for Colibri's own data.</summary>
    string DataDirectory { get; }

    /// <summary>SQLite database file.</summary>
    string DatabasePath { get; }

    /// <summary>JSON settings file.</summary>
    string SettingsPath { get; }

    /// <summary>Folder for rolling log files.</summary>
    string LogsDirectory { get; }

    /// <summary>aria2 session file (lets aria2 resume downloads after a restart).</summary>
    string Aria2SessionPath { get; }

    /// <summary>The user's Downloads folder.</summary>
    string DefaultDownloadsDirectory { get; }
}

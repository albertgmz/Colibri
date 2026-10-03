using System.Globalization;
using Colibri.Core.Engine;

namespace Colibri.Engine.Aria2;

/// <summary>
/// Command line for one aria2c launch. Each argument is a separate list entry (for
/// ProcessStartInfo.ArgumentList), so paths with spaces need no quoting.
/// </summary>
internal static class Aria2Arguments
{
    public static List<string> Build(
        int port,
        string configPath,
        int parentProcessId,
        string sessionPath,
        bool sessionExists,
        string logPath,
        EngineOptions options)
    {
        var arguments = new List<string>
        {
            "--enable-rpc",
            "--rpc-listen-all=false",
            $"--rpc-listen-port={port}",

            // The RPC secret is in this file, not on the command line: on Linux and macOS any local user
            // can read other users' command lines (ps, /proc/<pid>/cmdline). Naming our own file also
            // keeps aria2 from loading the user's personal ~/.aria2/aria2.conf.
            $"--conf-path={configPath}",
            "--rpc-max-request-size=4M",

            // aria2 exits by itself when this process ends, even if Colibri crashes or is killed,
            // so no orphaned aria2c keeps running in the background.
            $"--stop-with-process={parentProcessId}",
        };

        if (sessionExists)
        {
            arguments.Add($"--input-file={sessionPath}");
        }

        arguments.AddRange(
        [
            $"--save-session={sessionPath}",
            "--save-session-interval=30",
            "--continue=true",
            $"--max-connection-per-server={Aria2AddOptions.MaxConnectionsPerServer}",
            "--split=16",
            "--min-split-size=1M",
            "--allow-overwrite=false",
            "--auto-file-renaming=false",

            // "prealloc" writes the whole file up front (slow for big files) and "falloc" is not supported
            // on every file system; "none" works everywhere.
            "--file-allocation=none",
            "--check-integrity=false",
            "--console-log-level=warn",
            "--quiet=true",
            $"--log={logPath}",
            "--log-level=warn",
        ]);
        arguments.AddRange(GlobalOptions(options).Select(option => $"--{option.Key}={option.Value}"));

        return arguments;
    }

    /// <summary>
    /// The aria2 global options that follow <see cref="EngineOptions"/>. Used both on the command line
    /// and with aria2.changeGlobalOption while aria2 runs.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GlobalOptions(EngineOptions options) =>
    [
        new("max-concurrent-downloads", Math.Max(1, options.MaxConcurrentDownloads).ToString(CultureInfo.InvariantCulture)),

        // In bytes per second; 0 means unlimited.
        new("max-overall-download-limit", Math.Max(0, options.GlobalSpeedLimitBytesPerSecond).ToString(CultureInfo.InvariantCulture)),
    ];
}

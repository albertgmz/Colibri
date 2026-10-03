using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Colibri.Platform;

/// <summary>
/// Starts helper programs (xdg-open, open, osascript) without waiting for them.
/// </summary>
internal static class ProcessLauncher
{
    /// <summary>
    /// Starts <paramref name="program"/> with each argument passed as-is (no shell is involved, so file
    /// names cannot inject commands). Logs when the program cannot be started.
    /// </summary>
    public static void TryStart(string program, IEnumerable<string> arguments, ILogger logger)
    {
        var startInfo = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            // Not waited for: xdg-open can stay alive as long as the program it launched.
            using var process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not start {Program}", program);
        }
    }
}

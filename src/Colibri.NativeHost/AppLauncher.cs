using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Colibri.NativeHost;

/// <summary>Starts Colibri (hidden in the tray) when the browser needs it and it is not running.</summary>
internal static class AppLauncher
{
    /// <summary>
    /// Colibri's executable, next to this host: the build and publish copy the host into the app's folder
    /// (inside a macOS bundle that is <c>Contents/MacOS</c>, where both live). The host's own extension
    /// (".exe" on Windows, none elsewhere) is also the app's.
    /// </summary>
    public static string AppExecutablePath() =>
        Path.Combine(AppContext.BaseDirectory, "Colibri" + Path.GetExtension(Environment.ProcessPath ?? string.Empty));

    /// <summary>Starts Colibri with <c>--minimized</c> and does not wait for it. Returns false when it could not be started.</summary>
    public static bool TryStart(string executablePath, HostLog log)
    {
        if (!File.Exists(executablePath))
        {
            log.Error($"Colibri was not found at {executablePath}");
            return false;
        }

        // The browser waits for this host's stdin and stdout pipes to close (on Windows also for cmd.exe, which
        // may have started the host). A child holding a copy of them would keep the pipes open long after the
        // host exits, and anything it printed would corrupt the message stream. So the app gets fresh pipes for
        // its standard streams, closed on our side at once, and must not inherit the browser's handles: .NET
        // always lets a child inherit every inheritable handle on Windows, so ours are made non-inheritable
        // first. (On Linux and macOS only the standard streams are inherited, and they are replaced here.)
        if (OperatingSystem.IsWindows())
        {
            StopStandardHandleInheritance();
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
        };
        startInfo.ArgumentList.Add("--minimized");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();
            log.Info($"Started Colibri (process {process.Id})");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Could not start Colibri", ex);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void StopStandardHandleInheritance()
    {
        const int stdInput = -10, stdOutput = -11, stdError = -12;
        const uint handleFlagInherit = 1;
        foreach (var which in (int[])[stdInput, stdOutput, stdError])
        {
            var handle = GetStdHandle(which);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                _ = SetHandleInformation(handle, handleFlagInherit, 0);
            }
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
}

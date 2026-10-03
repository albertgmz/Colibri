using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Colibri.Platform.Shell;

/// <summary>
/// Windows: files open through ShellExecute (the program associated with the file type), and "show in
/// folder" uses the shell's own SHOpenFolderAndSelectItems, which selects the file in an Explorer window
/// (reusing one already showing that folder). This avoids <c>explorer.exe /select,"path"</c>, whose
/// command line parsing breaks on paths with commas and some other characters.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsShellService(ILogger<WindowsShellService> logger) : ShellServiceBase(logger)
{
    protected override Task OpenAsync(string path) => Task.Run(() =>
    {
        // UseShellExecute: Windows picks the default program for a file, or Explorer for a folder.
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    });

    protected override Task SelectAsync(string path) => RunOnStaThread(() =>
    {
        // A PIDL is the shell's own identifier for a file; it is allocated by the shell and freed by us.
        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _));
        try
        {
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    });

    /// <summary>
    /// Shell APIs need COM initialized on the calling thread, preferably as a single-threaded apartment,
    /// which thread pool threads are not. A short-lived thread of our own is set up that way.
    /// </summary>
    private static Task RunOnStaThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folderPidl, uint count, IntPtr itemPidls, uint flags);
}

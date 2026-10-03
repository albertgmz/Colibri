using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Colibri.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Colibri.Platform.Taskbar;

/// <summary>
/// Windows: the progress bar drawn behind the app's taskbar button, through the shell's ITaskbarList3 COM
/// interface. Must be called on the UI thread: the COM object is created there on first use (the UI
/// thread is a single-threaded apartment) and may only be used from that thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsTaskbarProgress(ILogger<WindowsTaskbarProgress> logger) : ITaskbarProgress
{
    // Progress is passed to Windows as "completed out of total"; 1000 steps are smoother than any taskbar button.
    private const ulong Steps = 1000;

    private ITaskbarList3? _taskbar;
    private bool _unavailable;

    public void SetProgress(nint windowHandle, double? fraction)
    {
        if (windowHandle == 0 || GetTaskbar() is not { } taskbar)
        {
            return;
        }

        try
        {
            if (fraction is { } value)
            {
                taskbar.SetProgressState(windowHandle, TaskbarProgressState.Normal);
                taskbar.SetProgressValue(windowHandle, (ulong)(Math.Clamp(value, 0, 1) * Steps), Steps);
            }
            else
            {
                taskbar.SetProgressState(windowHandle, TaskbarProgressState.NoProgress);
            }
        }
        catch (COMException ex)
        {
            // For example when Explorer restarts and the taskbar is briefly gone.
            logger.LogDebug(ex, "Could not set taskbar progress");
        }
    }

    private ITaskbarList3? GetTaskbar()
    {
        if (_taskbar is null && !_unavailable)
        {
            try
            {
                var taskbar = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbarListClassId, throwOnError: true)!)!;
                taskbar.HrInit();
                _taskbar = taskbar;
            }
            catch (Exception ex)
            {
                // No shell taskbar (for example Windows Server Core): stop trying.
                _unavailable = true;
                logger.LogInformation(ex, "Taskbar progress is not available");
            }
        }

        return _taskbar;
    }

    // CLSID_TaskbarList from ShObjIdl_core.h.
    private static readonly Guid TaskbarListClassId = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    // TBPFLAG values from ShObjIdl_core.h (only the two used).
    private enum TaskbarProgressState
    {
        NoProgress = 0,
        Normal = 0x2,
    }

    /// <summary>
    /// ITaskbarList3 as declared in ShObjIdl_core.h. COM calls methods by their position in the interface,
    /// so the methods of ITaskbarList and ITaskbarList2 that it extends are declared first, in order; the
    /// ones after SetProgressState are not needed and are left out.
    /// </summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();

        void AddTab(nint hwnd);

        void DeleteTab(nint hwnd);

        void ActivateTab(nint hwnd);

        void SetActiveAlt(nint hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        void SetProgressValue(nint hwnd, ulong completed, ulong total);

        void SetProgressState(nint hwnd, TaskbarProgressState state);
    }
}

using Colibri.Core.Platform;

namespace Colibri.Platform.Taskbar;

/// <summary>
/// Linux and macOS: no taskbar progress. Linux has no API that every desktop supports (the Unity launcher
/// API works only on some docks), and a progress badge on the macOS Dock is out of scope for now.
/// </summary>
internal sealed class NoTaskbarProgress : ITaskbarProgress
{
    public void SetProgress(nint windowHandle, double? fraction)
    {
    }
}

namespace Colibri.Core.Platform;

/// <summary>
/// Shows overall progress on the taskbar or dock icon, where the OS supports it.
/// </summary>
public interface ITaskbarProgress
{
    /// <summary>Sets progress from 0.0 to 1.0 for the given native window; null clears it.</summary>
    void SetProgress(nint windowHandle, double? fraction);
}

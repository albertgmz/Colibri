using Colibri.Core.Platform;

namespace Colibri.Platform.Paths;

/// <summary>DriveInfo has no async API; volume queries run off the UI thread.</summary>
public abstract class VolumeInfoService(StringComparison comparison) : IVolumeInfoService
{
    public Task<long?> GetAvailableBytesAsync(string folder, CancellationToken ct) => Task.Run<long?>(() =>
    {
        try
        {
            var path = Path.GetFullPath(folder);
            var drive = DriveInfo.GetDrives().Where(d => d.IsReady && IsOnVolume(path, d.Name))
                .OrderByDescending(d => d.Name.Length).FirstOrDefault();
            return drive?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }, ct);

    private bool IsOnVolume(string path, string root)
    {
        var name = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(name, comparison) || path.StartsWith(name + Path.DirectorySeparatorChar, comparison);
    }
}

public sealed class WindowsVolumeInfoService() : VolumeInfoService(StringComparison.OrdinalIgnoreCase);
public sealed class LinuxVolumeInfoService() : VolumeInfoService(StringComparison.Ordinal);
public sealed class MacVolumeInfoService() : VolumeInfoService(StringComparison.Ordinal);

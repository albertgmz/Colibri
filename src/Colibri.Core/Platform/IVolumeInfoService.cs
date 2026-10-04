namespace Colibri.Core.Platform;

public interface IVolumeInfoService
{
    /// <summary>Free bytes on the destination volume, or null if it is unavailable.</summary>
    Task<long?> GetAvailableBytesAsync(string folder, CancellationToken ct);
}

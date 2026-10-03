using Colibri.Platform.Paths;

namespace Colibri.Platform.Tests;

public class VolumeInfoTests
{
    [Fact]
    public async Task Existing_destination_reports_nonnegative_space_without_creating_it()
    {
        // This exercises the runtime's volume enumeration; no network or writes are involved.
        VolumeInfoService service = OperatingSystem.IsWindows() ? new WindowsVolumeInfoService() : new LinuxVolumeInfoService();
        var free = await service.GetAvailableBytesAsync(Path.GetTempPath(), TestContext.Current.CancellationToken);
        Assert.NotNull(free);
        Assert.True(free >= 0);
    }

    [Fact]
    public async Task Invalid_destination_is_unknown()
    {
        var service = new WindowsVolumeInfoService();
        Assert.Null(await service.GetAvailableBytesAsync("\0", TestContext.Current.CancellationToken));
    }
}

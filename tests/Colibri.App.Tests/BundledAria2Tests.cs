namespace Colibri.App.Tests;

public class BundledAria2Tests
{
    [Fact]
    public void Windows_builds_ship_aria2_with_its_licence_files()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "aria2 is bundled on Windows only");
        var folder = Path.Combine(AppContext.BaseDirectory, "aria2");

        // aria2 is GPLv2 with an exception for OpenSSL; both texts travel with the binary.
        Assert.True(File.Exists(Path.Combine(folder, "aria2c.exe")));
        Assert.True(File.Exists(Path.Combine(folder, "COPYING")));
        Assert.True(File.Exists(Path.Combine(folder, "LICENSE.OpenSSL")));
    }
}

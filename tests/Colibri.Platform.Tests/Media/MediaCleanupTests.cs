using Colibri.Core.Engine;
using Colibri.Core.Media;
using Colibri.Core.Models;
using Colibri.Core.Settings;
using Colibri.Platform.Media;

namespace Colibri.Platform.Tests.Media;

public sealed class MediaCleanupTests
{
    [Fact]
    public async Task PersistedOwnedWorkspaceCleanupRetainsUnknownUserFiles()
    {
        // macOS's temporary directory can contain system symlink ancestors. Use
        // this owned test output so the success case exercises a link-free path.
        var folder = Path.Combine(AppContext.BaseDirectory, "colibri-media-cleanup-" + Guid.NewGuid().ToString("N"));
        var handle = Guid.NewGuid().ToString("N");
        var work = Path.Combine(folder, ".colibri-media-" + handle);
        Directory.CreateDirectory(work);
        try
        {
            foreach (var name in new[] { "primary.bin", "audio.bin", "result.mp4", "unknown.txt" })
                await File.WriteAllTextAsync(Path.Combine(work, name), "fixture", TestContext.Current.CancellationToken);
            IEngineFileCleanup helper = new YtDlpMediaHelper(new AppSettings());
            await helper.DeleteOwnedPartialFilesAsync(new DownloadItem
            {
                EngineId = "media-helper", EngineHandle = handle, SaveFolder = folder,
                MediaSelection = new("video", null, "mp4")
            }, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "unknown.txt" }, Directory.GetFiles(work).Select(Path.GetFileName));
        }
        finally
        {
            // This test created every file under its fresh random root.
            Directory.Delete(folder, recursive: true);
        }
    }
}

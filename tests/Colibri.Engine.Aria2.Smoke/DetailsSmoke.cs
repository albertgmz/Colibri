using Colibri.Core.Engine;
using Colibri.Core.Models;

namespace Colibri.Engine.Aria2.Smoke;

/// <summary>Run against an isolated local range server; the v1 lifecycle smoke stays unchanged.</summary>
internal static class DetailsSmoke
{
    public static async Task<int> RunAsync(Uri url)
    {
        var root = Path.Combine(Path.GetTempPath(), "colibri-details-smoke-" + Guid.NewGuid().ToString("N"));
        var paths = new SmokePaths(root);
        var options = new EngineOptions(3, 8, 0);
        using var engine = new Aria2Engine(new SmokeAria2Locator(), paths, new ConsoleLogger<Aria2Engine>(), () => new(null, options));
        var failures = 0;
        void Check(string name, bool passed)
        {
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
            if (!passed) failures++;
        }
        try
        {
            await engine.StartAsync(CancellationToken.None);
            Check("engine running", engine.State == EngineState.Running);
            var handle = await engine.AddAsync(new DownloadRequest { Uri = url, TransferOptions = new(32768, 2) },
                paths.DefaultDownloadsDirectory, "details.bin", null, false, CancellationToken.None);
            EngineDownloadStatus? status = null;
            EngineDownloadDetails? details = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            do
            {
                status = await engine.GetStatusAsync(handle, timeout.Token);
                details = await engine.GetDetailsAsync(handle, timeout.Token);
                if (status is { CompletedBytes: > 0, NumPieces: > 0 } && details?.Servers.Count > 0) break;
                await Task.Delay(100, timeout.Token);
            } while (true);
            Check("real getServers reports current server host and speed", details!.Servers.All(s =>
                !string.IsNullOrWhiteSpace(s.Host) && s.BytesPerSecond >= 0 && Uri.TryCreate(s.CurrentUrl, UriKind.Absolute, out _)));
            Check("piece map and piece size are exposed", status!.PieceLength is > 0
                && PieceMap.CompletedCount(status.Bitfield, status.NumPieces) is not null);
            Check("add applies persisted download options", details.Options == new DownloadTransferOptions(32768, 2));
            await engine.ApplyDownloadOptionsAsync(handle, new(65536, 3), CancellationToken.None);
            details = await engine.GetDetailsAsync(handle, CancellationToken.None);
            Check("real changeOption/getOption round trip", details!.Options == new DownloadTransferOptions(65536, 3));
            await engine.PauseAsync(handle, CancellationToken.None);
            using var pausedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await engine.GetStatusAsync(handle, pausedTimeout.Token))?.State != EngineDownloadState.Paused)
                await Task.Delay(50, pausedTimeout.Token);
            details = await engine.GetDetailsAsync(handle, pausedTimeout.Token);
            Check("paused transfer exposes options without active servers", details!.Servers.Count == 0
                && details.Options == new DownloadTransferOptions(65536, 3));
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
            // All output is confined to this freshly-created temporary directory.
            Directory.Delete(root, recursive: true);
        }
    }
}

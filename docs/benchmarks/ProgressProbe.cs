using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Colibri.App.Services;
using Colibri.App.ViewModels;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Colibri.App;

internal static class RuntimeProbe
{
    internal static async Task RunAsync(IServiceProvider services, DesktopShell shell)
    {
        if (Environment.GetEnvironmentVariable("COLIBRI_BENCH_MODE") != "progress") return;
        var manager = services.GetRequiredService<DownloadManager>();
        var engine = services.GetRequiredService<IDownloadEngine>();
        var view = services.GetRequiredService<MainWindowViewModel>();
        var root = Environment.GetEnvironmentVariable("COLIBRI_BENCH_DATA")!;
        var port = Environment.GetEnvironmentVariable("COLIBRI_PROBE_PORT")!;
        var first = (await manager.AddAsync($"http://127.0.0.1:{port}/first.bin", LinkContext.Empty, null, null, CancellationToken.None)).Single();
        var clock = Stopwatch.StartNew();
        var samples = new List<object>();
        for (var index = 0; index < 65; index++)
        {
            if (index == 10) await manager.AddAsync($"http://127.0.0.1:{port}/second.bin", LinkContext.Empty, null, null, CancellationToken.None);
            var rpc = await engine.GetStatusAsync(first.EngineHandle!, CancellationToken.None);
            var current = (await manager.GetItemsAsync(CancellationToken.None)).Single(item => item.Id == first.Id);
            var ui = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var row = view.AllItems.SingleOrDefault(row => row.Id == first.Id);
                var window = ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow;
                return new { bytes = row?.CompletedBytes, speed = row?.Speed, percent = row?.ProgressValue,
                    text = row?.ProgressText, windowVisible = window?.IsVisible, state = window?.WindowState.ToString() };
            });
            var payload = await Task.Run(() => CountPayload(Path.Combine(first.SaveFolder, first.FileName)));
            samples.Add(new { index, elapsedMs = clock.ElapsedMilliseconds, secondAdded = index >= 10,
                rpcBytes = rpc?.CompletedBytes, rpcSpeed = rpc?.DownloadSpeed, coreBytes = current.CompletedBytes,
                coreSpeed = current.DownloadSpeed, payloadBytes = payload, ui });
            await File.WriteAllTextAsync(Path.Combine(root, "progress-samples.json"), JsonSerializer.Serialize(samples));
            await Task.Delay(1000);
        }
        await manager.PauseAllAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(root, "probe-complete.txt"), "Complete; isolated downloads paused.");
    }

    private static long CountPayload(string path)
    {
        if (!File.Exists(path)) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
            for (var i = 0; i < read; i++) if (buffer[i] == 0x5A) total++;
        return total;
    }
}

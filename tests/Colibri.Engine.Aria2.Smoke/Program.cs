// End-to-end check of Aria2Engine against a real aria2c and a real download:
// add -> pause -> crash aria2 while paused -> automatic restart restores the download from the session
// -> resume -> complete -> remove -> graceful shutdown. Prints PASS/FAIL lines; exit code 0 or 1.
using System.Collections.Concurrent;
using System.Diagnostics;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Engine.Aria2;
using Colibri.Engine.Aria2.Smoke;

var url = new Uri(args.Length > 0 ? args[0] : "https://fsn1-speed.hetzner.com/100MB.bin");
var root = Path.Combine(Path.GetTempPath(), "colibri-smoke-" + Guid.NewGuid().ToString("N")[..8]);
var paths = new SmokePaths(root);
var failures = 0;

void Check(string name, bool passed, string detail = "")
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " - " + detail : "")}");
    if (!passed)
    {
        failures++;
    }
}

static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
{
    var elapsed = Stopwatch.StartNew();
    while (elapsed.Elapsed < timeout)
    {
        if (await condition())
        {
            return true;
        }

        await Task.Delay(250);
    }

    return false;
}

static bool HasExited(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return process.HasExited;
    }
    catch (ArgumentException)
    {
        return true; // No such process any more.
    }
}

static bool FileContains(string path, string text)
{
    try
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd().Contains(text, StringComparison.Ordinal);
    }
    catch (IOException)
    {
        return false;
    }
}

Console.WriteLine($"Data folder: {root}");
Console.WriteLine($"URL:         {url}");

var options = new EngineOptions(MaxConcurrentDownloads: 3, ConnectionsPerServer: 16, GlobalSpeedLimitBytesPerSecond: 0);
using var engine = new Aria2Engine(new SmokeAria2Locator(), paths, new ConsoleLogger<Aria2Engine>(), () => new Aria2Settings(null, options));
var states = new ConcurrentQueue<EngineState>();
engine.StateChanged += (_, state) => states.Enqueue(state);
var events = new ConcurrentQueue<EngineDownloadEvent>();
engine.DownloadEvent += (_, e) => events.Enqueue(e);
var ct = CancellationToken.None;

try
{
    // 1. Start.
    await engine.StartAsync(ct);
    Check("engine starts", engine.State == EngineState.Running, $"state {engine.State}");
    if (engine.State != EngineState.Running)
    {
        return 1;
    }

    Check("config file with the RPC secret is deleted once aria2 runs", !File.Exists(Path.Combine(root, "aria2-rpc.conf")));
    var firstPid = engine.Aria2ProcessId!.Value;

    // 2. Add and wait for the first bytes.
    var fileName = Path.GetFileName(url.LocalPath) is { Length: > 0 } name ? name : "download.bin";
    var gid = await engine.AddAsync(new DownloadRequest { Uri = url }, paths.DefaultDownloadsDirectory, fileName, ct);
    Check("download added", gid.Length == 16, $"gid {gid}");

    EngineDownloadStatus? status = null;
    var started = await WaitUntilAsync(async () =>
    {
        status = await engine.GetStatusAsync(gid, ct);
        return status is { CompletedBytes: > 0 };
    }, TimeSpan.FromSeconds(60));
    Check("bytes are downloading", started, $"{status?.CompletedBytes:N0} of {status?.TotalBytes:N0} bytes");

    // 3. Pause; bytes must stop increasing.
    await engine.PauseAsync(gid, ct);
    var paused = await WaitUntilAsync(async () => (await engine.GetStatusAsync(gid, ct))?.State == EngineDownloadState.Paused, TimeSpan.FromSeconds(10));
    var before = (await engine.GetStatusAsync(gid, ct))!.CompletedBytes;
    await Task.Delay(TimeSpan.FromSeconds(3));
    var after = (await engine.GetStatusAsync(gid, ct))!.CompletedBytes;
    Check("pause -> state Paused", paused);
    Check("paused bytes stay still", before == after && before > 0, $"{before:N0} -> {after:N0}");
    Check("pause notification received", events.Contains(new EngineDownloadEvent(gid, EngineDownloadEventKind.Paused)));

    // 4. Crash aria2 while paused. aria2 saves the session every 30 s, so wait until the download is in it.
    var inSession = await WaitUntilAsync(() => Task.FromResult(FileContains(paths.Aria2SessionPath, gid)), TimeSpan.FromSeconds(40));
    Check("download written to the session file", inSession);

    states.Clear();
    using (var child = Process.GetProcessById(firstPid))
    {
        child.Kill();
    }

    var restarted = await WaitUntilAsync(
        () => Task.FromResult(engine.State == EngineState.Running && engine.Aria2ProcessId is { } pid && pid != firstPid),
        TimeSpan.FromSeconds(30));
    Check("killed aria2 is restarted", restarted, $"states: {string.Join(" -> ", states)}; pid {firstPid} -> {engine.Aria2ProcessId}");

    // A restored paused download reports 0 bytes until it is resumed.
    var restored = await engine.GetStatusAsync(gid, ct);
    Check("download restored from the session", restored?.State == EngineDownloadState.Paused,
        $"state {restored?.State}, {restored?.CompletedBytes:N0} bytes");

    // 5. Resume and wait for completion.
    await engine.ResumeAsync(gid, ct);
    var resumed = await WaitUntilAsync(async () => (await engine.GetStatusAsync(gid, ct))?.State is EngineDownloadState.Active or EngineDownloadState.Complete, TimeSpan.FromSeconds(10));
    Check("resume -> state Active", resumed);

    var lastPrint = Stopwatch.StartNew();
    var completed = await WaitUntilAsync(async () =>
    {
        status = await engine.GetStatusAsync(gid, ct);
        if (lastPrint.Elapsed > TimeSpan.FromSeconds(5))
        {
            Console.WriteLine($"  ... {status?.CompletedBytes:N0} of {status?.TotalBytes:N0} bytes, {status?.DownloadSpeed / 1024:N0} KiB/s, {status?.Connections} connections");
            lastPrint.Restart();
        }

        return status?.State is EngineDownloadState.Complete or EngineDownloadState.Error;
    }, TimeSpan.FromMinutes(15));
    Check("download completes", completed && status!.State == EngineDownloadState.Complete, $"state {status?.State} {status?.ErrorMessage}");

    var filePath = status?.FilePath;
    var sizeOnDisk = filePath is not null && File.Exists(filePath) ? new FileInfo(filePath).Length : -1;
    Check("file size on disk equals total length", sizeOnDisk == status?.TotalBytes && sizeOnDisk > 0,
        $"{filePath}: {sizeOnDisk:N0} bytes, expected {status?.TotalBytes:N0}");
    Check("completed notification received", await WaitUntilAsync(
        () => Task.FromResult(events.Contains(new EngineDownloadEvent(gid, EngineDownloadEventKind.Completed))), TimeSpan.FromSeconds(5)));

    // 6. Remove: aria2 forgets the download, the file stays.
    await engine.RemoveAsync(gid, ct);
    Check("removed download is unknown to aria2", await engine.GetStatusAsync(gid, ct) is null);
    Check("remove keeps the file", filePath is not null && File.Exists(filePath));

    // 7. Graceful shutdown.
    var lastPid = engine.Aria2ProcessId!.Value;
    await engine.StopAsync(ct);
    Check("engine stops", engine.State == EngineState.Stopped);
    Check("aria2 process exited", await WaitUntilAsync(() => Task.FromResult(HasExited(lastPid)), TimeSpan.FromSeconds(5)), $"pid {lastPid}");
    Check("session file exists", File.Exists(paths.Aria2SessionPath));
    await engine.StopAsync(ct);
    Check("second stop is harmless", engine.State == EngineState.Stopped);
}
catch (Exception ex)
{
    Check("no unexpected exception", false, ex.ToString());
}

Console.WriteLine(failures == 0 ? "SMOKE TEST PASSED" : $"SMOKE TEST FAILED ({failures} checks)");
if (failures == 0)
{
    Directory.Delete(root, recursive: true);
}
else
{
    Console.WriteLine($"Data kept for inspection in {root}");
}

return failures == 0 ? 0 : 1;

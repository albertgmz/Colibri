using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Colibri.Core.Engine;
using Colibri.Core.Media;
using Colibri.Core.Models;
using Colibri.Core.Network;
using Colibri.Core.Settings;
using Colibri.Core.Services;

namespace Colibri.Platform.Media;

/// <summary>Explicit configured tools only. Process output and source URLs never enter logs.</summary>
public sealed class YtDlpMediaHelper(AppSettings settings) : IMediaHelper, IDownloadEngine, IEngineFileCleanup
{
    private sealed class Transfer(DownloadRequest request, string folder, string name, string handle)
    {
        public DownloadRequest Request { get; set; } = request;
        public string Folder { get; } = folder;
        public string Name { get; } = name;
        public string Handle { get; } = handle;
        public EngineDownloadStatus Status { get; set; } = new(handle, EngineDownloadState.Paused, 0, 0, 0, 0);
        public CancellationTokenSource? Cancellation { get; set; }
        public Task? Operation { get; set; }
        public bool QueueAfterReconfigure { get; set; }
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Transfer> _transfers = new();
    private EngineOptions _options = new(3, 1, 0);
    public string Id => "media-helper";
    public EngineState State { get; private set; } = EngineState.Stopped;
    public event EventHandler<EngineDownloadEvent>? DownloadEvent;
    public event EventHandler<EngineState>? StateChanged;
    public bool CanHandle(DownloadRequest request) => request.MediaSelection is not null;
    public string CreateHandle() => Guid.NewGuid().ToString("N");
    public Task StartAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); State = EngineState.Running; StateChanged?.Invoke(this, State); return Task.CompletedTask; }
    public async Task StopAsync(CancellationToken ct)
    {
        string[] handles;
        lock (_sync) { State = EngineState.Stopped; handles = _transfers.Keys.ToArray(); }
        foreach (var handle in handles) await PauseAsync(handle, CancellationToken.None);
        StateChanged?.Invoke(this, State);
    }
    public async Task ApplyOptionsAsync(EngineOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Transfer[] excess;
        lock (_sync) {
            _options = options;
            excess = _transfers.Values.Where(t => t.Operation is not null).Skip(EffectiveConcurrency()).ToArray();
            foreach (var transfer in excess) { transfer.QueueAfterReconfigure = true; transfer.Cancellation?.Cancel(); }
        }
        foreach (var transfer in excess) if (transfer.Operation is { } operation) await operation;
        lock (_sync) {
            foreach (var transfer in excess) if (transfer.QueueAfterReconfigure && transfer.Status.State == EngineDownloadState.Paused) {
                transfer.QueueAfterReconfigure = false; transfer.Status = transfer.Status with { State = EngineDownloadState.Waiting };
            }
            Pump();
        }
    }
    public async Task<MediaToolInfo> ValidateToolsAsync(CancellationToken ct)
    {
        RequireEnabled();
        var tool = RequireExecutable(settings.YtDlpPath);
        var version = (await CaptureAsync(tool, ["--ignore-config", "--no-plugin-dirs", "--no-remote-components", "--version"], null, 4096, TimeSpan.FromSeconds(10), ct)).Trim();
        if (!Regex.IsMatch(version, "^[0-9]{4}\\.[0-9]{2}\\.[0-9]{2}(?:\\.[0-9]+)?$", RegexOptions.CultureInvariant))
            throw new MediaHelperException("The configured executable is not a supported yt-dlp version.");
        var help = await CaptureAsync(tool, ["--ignore-config", "--no-plugin-dirs", "--no-remote-components", "--help"], null, 256 * 1024, TimeSpan.FromSeconds(10), ct);
        if (!help.Contains("--config-locations", StringComparison.Ordinal) || !help.Contains("--no-plugin-dirs", StringComparison.Ordinal) || !help.Contains("--no-remote-components", StringComparison.Ordinal))
            throw new MediaHelperException("The configured yt-dlp version lacks required safe configuration options.");
        var licensePath = settings.YtDlpLicensePath;
        if (string.IsNullOrWhiteSpace(licensePath)) licensePath = new[] { "LICENSE", "COPYING", "LICENSE.txt" }
            .Select(name => Path.Combine(Path.GetDirectoryName(tool)!, name)).FirstOrDefault(File.Exists) ?? "";
        if (!Path.IsPathFullyQualified(licensePath) || !File.Exists(licensePath) || new FileInfo(licensePath).Length > 256 * 1024)
            throw new MediaHelperException("Configure a bounded yt-dlp license file from the installed distribution.");
        var license = await ReadLicenseAsync(licensePath, ct);
        var licenseName = license.Contains("GNU GENERAL PUBLIC LICENSE", StringComparison.OrdinalIgnoreCase) ? "GPL (installed distribution)" :
            license.Contains("Unlicense", StringComparison.OrdinalIgnoreCase) || license.Contains("free and unencumbered software released into the public domain", StringComparison.OrdinalIgnoreCase) ? "Unlicense (installed distribution)" :
            throw new MediaHelperException("The configured yt-dlp license was not recognized.");
        string? ffVersion = null, ffLicense = null;
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath)) {
            var ffmpeg = RequireExecutable(settings.FfmpegPath);
            if (!Path.GetFileNameWithoutExtension(ffmpeg).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
                throw new MediaHelperException("The configured muxer must be named ffmpeg.");
            var output = await CaptureAsync(ffmpeg, ["-version"], null, 64 * 1024, TimeSpan.FromSeconds(10), ct);
            ffVersion = output.Split('\n')[0].Trim();
            if (!ffVersion.StartsWith("ffmpeg version ", StringComparison.Ordinal) || ffVersion.Length > 512)
                throw new MediaHelperException("The configured muxer did not report an FFmpeg version.");
            output = await CaptureAsync(ffmpeg, ["-L"], null, 64 * 1024, TimeSpan.FromSeconds(10), ct);
            ffLicense = output.Contains("Lesser General Public License", StringComparison.OrdinalIgnoreCase) ? "LGPL (configured FFmpeg)" :
                output.Contains("General Public License", StringComparison.OrdinalIgnoreCase) ? "GPL (configured FFmpeg)" :
                throw new MediaHelperException("The configured FFmpeg license was not recognized.");
        }
        return new(version, licenseName, ffVersion, ffLicense);
    }
    public async Task<MediaMetadata> InspectAsync(Uri source, LinkContext context, DownloadNetworkPolicy? network, CancellationToken ct)
    {
        ValidateSource(source); MediaPolicy.Validate(network ?? settings.DefaultNetworkPolicy, context);
        await ValidateToolsAsync(ct);
        var json = await InspectJsonAsync(source, context, ct);
        var metadata = MediaMetadataParser.Parse(json);
        await MediaManifestGuard.CheckAsync(json, ct);
        return metadata;
    }
    private Task<string> InspectJsonAsync(Uri source, LinkContext context, CancellationToken ct) => CaptureAsync(RequireExecutable(settings.YtDlpPath), BaseArguments().Concat(["--dump-single-json", "--skip-download"]).ToArray(),
            BuildConfiguration(source, context), MediaMetadataParser.MaxCharacters, TimeSpan.FromSeconds(60), ct);
    public async Task<string> AddAsync(DownloadRequest request, string saveFolder, string fileName, string? handle, bool startPaused, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireEnabled(); ValidateSource(request.Uri);
        request.MediaSelection?.Validate();
        if (request.MediaSelection is null) throw new MediaHelperException("Choose a media format first.");
        MediaPolicy.Validate(request.NetworkPolicy ?? settings.DefaultNetworkPolicy, Context(request));
        await ValidateToolsAsync(ct);
        if (request.MediaSelection.RequiresMux && string.IsNullOrWhiteSpace(settings.FfmpegPath)) throw new MediaHelperException("This selection needs a configured FFmpeg muxer.");
        if (!Path.IsPathFullyQualified(saveFolder) || FileNameSanitizer.Sanitize(fileName) != fileName || Path.GetExtension(fileName) != "." + request.MediaSelection.OutputExtension)
            throw new MediaHelperException("Invalid media output path.");
        MediaPathSafety.RequireNoLinks(saveFolder); MediaPathSafety.RequireNoLinks(Path.Combine(saveFolder, fileName));
        lock (_sync) {
            if (State != EngineState.Running) throw new EngineOperationException("Media engine is stopped.");
            handle ??= CreateHandle();
            if (!Guid.TryParseExact(handle, "N", out _)) throw new MediaHelperException("Invalid media transfer handle.");
            if (_transfers.ContainsKey(handle)) return handle;
            var transfer = new Transfer(request, saveFolder, fileName, handle);
            transfer.Status = transfer.Status with { State = startPaused ? EngineDownloadState.Paused : EngineDownloadState.Waiting, FilePath = Path.Combine(saveFolder, fileName) };
            _transfers.Add(handle, transfer); Pump(); return handle;
        }
    }
    public async Task PauseAsync(string handle, CancellationToken ct)
    {
        Transfer transfer; Task? operation;
        lock (_sync) {
            transfer = Find(handle); transfer.QueueAfterReconfigure = false;
            if (transfer.Status.State is EngineDownloadState.Complete or EngineDownloadState.Error or EngineDownloadState.Removed) return;
            transfer.Cancellation?.Cancel(); operation = transfer.Operation;
            if (operation is null) transfer.Status = transfer.Status with { State = EngineDownloadState.Paused, DownloadSpeed = 0, Connections = 0 };
        }
        // Await owned process-tree termination even if the caller cancels; a pause must prove stop.
        if (operation is not null) await operation;
        DownloadEvent?.Invoke(this, new(handle, EngineDownloadEventKind.Paused));
    }
    public Task ResumeAsync(string handle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync) { var transfer = Find(handle); if (transfer.Operation is not null || transfer.Status.State == EngineDownloadState.Complete) return Task.CompletedTask;
            RequireEnabled(); MediaPolicy.Validate(transfer.Request.NetworkPolicy ?? settings.DefaultNetworkPolicy, Context(transfer.Request));
            transfer.Status = transfer.Status with { State = EngineDownloadState.Waiting, ErrorMessage = null }; Pump(); }
        return Task.CompletedTask;
    }
    public Task DeleteOwnedPartialFilesAsync(DownloadItem item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (item.EngineId != Id || !Guid.TryParseExact(item.EngineHandle, "N", out _) || item.MediaSelection is not { } selection || !Path.IsPathFullyQualified(item.SaveFolder))
            throw new MediaHelperException("The media temporary-file ownership is invalid.");
        selection.Validate();
        var work = Path.Combine(item.SaveFolder, ".colibri-media-" + item.EngineHandle);
        MediaPathSafety.RequireNoLinks(work);
        if (!Directory.Exists(work)) return Task.CompletedTask;
        var owned = new[] { "primary.bin", "audio.bin", "result." + selection.OutputExtension }.Select(name => Path.Combine(work, name)).ToArray();
        foreach (var path in owned) MediaPathSafety.RequireNoLinks(path);
        foreach (var path in owned) { ct.ThrowIfCancellationRequested(); if (File.Exists(path)) File.Delete(path); }
        if (!Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work);
        return Task.CompletedTask;
    }
    public async Task RemoveAsync(string handle, CancellationToken ct) { await PauseAsync(handle, ct); lock (_sync) _transfers.Remove(handle); }
    public async Task ApplyNetworkPolicyAsync(string handle, DownloadNetworkPolicy? policy, CancellationToken ct)
    {
        await PauseAsync(handle, CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        MediaPolicy.Validate(policy ?? settings.DefaultNetworkPolicy, LinkContext.Empty);
        lock (_sync) { var transfer = Find(handle); transfer.Request = transfer.Request with { NetworkPolicy = policy }; }
    }
    public Task<EngineDownloadStatus?> GetStatusAsync(string handle, CancellationToken ct) { lock (_sync) return Task.FromResult(_transfers.GetValueOrDefault(handle)?.Status); }
    public Task<IReadOnlyList<EngineDownloadStatus>> GetAllAsync(CancellationToken ct) { lock (_sync) return Task.FromResult<IReadOnlyList<EngineDownloadStatus>>(_transfers.Values.Select(t => t.Status).ToArray()); }
    public Task<EngineGlobalStats> GetGlobalStatsAsync(CancellationToken ct) { lock (_sync) return Task.FromResult(new EngineGlobalStats(_transfers.Values.Sum(t => t.Status.DownloadSpeed), _transfers.Values.Count(t => t.Status.State == EngineDownloadState.Active), _transfers.Values.Count(t => t.Status.State == EngineDownloadState.Waiting))); }
    private int EffectiveConcurrency() => _options.GlobalSpeedLimitBytesPerSecond > 0
        ? (int)Math.Min(Math.Max(1, _options.MaxConcurrentDownloads), _options.GlobalSpeedLimitBytesPerSecond)
        : Math.Max(1, _options.MaxConcurrentDownloads);
    private long TransferRate(Transfer transfer)
    {
        var global = _options.GlobalSpeedLimitBytesPerSecond > 0 ? _options.GlobalSpeedLimitBytesPerSecond / EffectiveConcurrency() : 0;
        var individual = transfer.Request.TransferOptions?.SpeedLimitBytesPerSecond ?? 0;
        return global > 0 && individual > 0 ? Math.Min(global, individual) : Math.Max(global, individual);
    }
    public Task ApplyDownloadOptionsAsync(string handle, DownloadTransferOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (options.ConnectionsPerServer != 1 || options.SpeedLimitBytesPerSecond < 0)
            throw new EngineOperationException("Media uses one segment connection per transfer; choose one connection and a nonnegative rate.");
        lock (_sync) { var transfer = Find(handle); transfer.Request = transfer.Request with { TransferOptions = options }; }
        return Task.CompletedTask;
    }
    public Task<EngineDownloadDetails?> GetDetailsAsync(string handle, CancellationToken ct)
    {
        lock (_sync) { var transfer = Find(handle); return Task.FromResult<EngineDownloadDetails?>(new([], transfer.Request.TransferOptions ?? new(0, 1))); }
    }
    private Transfer Find(string handle) => _transfers.GetValueOrDefault(handle) ?? throw new EngineOperationException("Unknown media transfer.");
    private void Pump()
    {
        if (State != EngineState.Running) return;
        var slots = EffectiveConcurrency() - _transfers.Values.Count(t => t.Operation is not null);
        foreach (var transfer in _transfers.Values.Where(t => t.Operation is null && t.Status.State == EngineDownloadState.Waiting).Take(Math.Max(0, slots))) {
            transfer.Cancellation = new(); transfer.Status = transfer.Status with { State = EngineDownloadState.Active, Connections = 1 };
            transfer.Operation = Task.Run(() => TransferAsync(transfer, transfer.Cancellation.Token));
        }
    }
    private async Task TransferAsync(Transfer transfer, CancellationToken ct)
    {
        EngineDownloadEventKind outcome = EngineDownloadEventKind.Error;
        try {
            RequireEnabled(); var context = Context(transfer.Request);
            MediaPolicy.Validate(transfer.Request.NetworkPolicy ?? settings.DefaultNetworkPolicy, context);
            await ValidateToolsAsync(ct);
            // Formats and expiring URLs are resolved again, but the resulting segment URLs are an immutable snapshot.
            var json = await InspectJsonAsync(transfer.Request.Uri, context, ct);
            var metadata = MediaMetadataParser.Parse(json);
            await MediaManifestGuard.CheckAsync(json, ct);
            var selection = transfer.Request.MediaSelection!;
            var video = metadata.Formats.FirstOrDefault(f => f.Id == selection.FormatId) ?? throw new MediaHelperException("The selected media format is no longer available.");
            if (selection.AudioFormatId is { } audioId && (!video.HasVideo || video.HasAudio || !metadata.Formats.Any(f => f.Id == audioId && !f.HasVideo && f.HasAudio)))
                throw new MediaHelperException("The selected audio/video combination is no longer available.");
            if (!selection.RequiresMux && video.Extension != selection.OutputExtension) throw new MediaHelperException("The media output format changed; inspect and select it again.");
            var output = Path.Combine(transfer.Folder, transfer.Name);
            var work = Path.Combine(transfer.Folder, ".colibri-media-" + transfer.Handle);
            MediaPathSafety.RequireNoLinks(work); MediaPathSafety.RequireNoLinks(output);
            Directory.CreateDirectory(work);
            if ((new DirectoryInfo(work).Attributes & FileAttributes.ReparsePoint) != 0) throw new MediaHelperException("The media workspace must be an owned local directory.");
            using var downloader = new MediaSnapshotDownloader(context, () => TransferRate(transfer));
            async Task<MediaSourcePlan> Plan(string formatId) {
                var plan = MediaSegmentPlans.ParseFormat(json, formatId);
                // HLS URLs need not end in .m3u8; select by the metadata protocol instead.
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var format = doc.RootElement.GetProperty("formats").EnumerateArray().First(f => f.GetProperty("format_id").GetString() == formatId);
                var protocol = format.TryGetProperty("protocol", out var p) ? p.GetString() : "https";
                if (protocol is "m3u8" or "m3u8_native") {
                    var source = MediaSegmentPlans.RequireHttp(format.GetProperty("url").GetString()!);
                    var manifest = await downloader.ReadManifestAsync(source, ct);
                    plan = MediaSegmentPlans.ParseHls(manifest.FinalUri, manifest.Text);
                }
                return plan;
            }
            var primary = await Plan(selection.FormatId);
            MediaSourcePlan? audio = selection.AudioFormatId is { } audioFormat ? await Plan(audioFormat) : null;
            if ((primary.NeedsRemux || audio is not null) && string.IsNullOrWhiteSpace(settings.FfmpegPath)) throw new MediaHelperException("Segmented media and muxing require a configured FFmpeg executable.");
            var first = Path.Combine(work, "primary.bin"); var second = Path.Combine(work, "audio.bin"); var staged = Path.Combine(work, "result." + selection.OutputExtension);
            foreach (var path in new[] { first, second, staged, output }) MediaPathSafety.RequireNoLinks(path);
            long prior = 0; var clock = Stopwatch.StartNew(); long lastBytes = 0; var lastTime = TimeSpan.Zero;
            void Progress(long completed, long total) {
                var bytes = prior + completed; var elapsed = clock.Elapsed - lastTime;
                if (elapsed < TimeSpan.FromMilliseconds(250)) return;
                var rate = (long)Math.Max(0, (bytes - lastBytes) / elapsed.TotalSeconds); lastBytes = bytes; lastTime = clock.Elapsed;
                lock (_sync) transfer.Status = transfer.Status with { CompletedBytes = bytes, TotalBytes = audio is null ? total : 0, DownloadSpeed = rate };
            }
            prior = await downloader.DownloadAsync(primary, first, Progress, ct);
            if (audio is not null) await downloader.DownloadAsync(audio, second, Progress, ct);
            if (primary.NeedsRemux || audio is not null) {
                foreach (var path in new[] { first, second, staged }) MediaPathSafety.RequireNoLinks(path);
                var args = new List<string> { "-nostdin", "-v", "error", "-protocol_whitelist", "file", "-i", first };
                if (audio is not null) args.AddRange(["-protocol_whitelist", "file", "-i", second, "-map", "0:v:0", "-map", "1:a:0"]);
                args.AddRange(["-c", "copy", "-y", staged]);
                await RunAsync(RequireExecutable(settings.FfmpegPath), args, null, _ => { }, TimeSpan.FromHours(2), ct);
            } else File.Move(first, staged, overwrite: true);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(staged) || new FileInfo(staged).Length == 0) throw new MediaHelperException("The media helper did not produce a complete local output.");
            MediaPathSafety.RequireNoLinks(staged); MediaPathSafety.RequireNoLinks(output);
            File.Move(staged, output, overwrite: false);
            foreach (var path in new[] { first, second }) if (File.Exists(path)) File.Delete(path);
            if (!Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work);
            var size = new FileInfo(output).Length;
            lock (_sync) transfer.Status = transfer.Status with { State = EngineDownloadState.Complete, TotalBytes = size, CompletedBytes = size, DownloadSpeed = 0, Connections = 0 };
            outcome = EngineDownloadEventKind.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { lock (_sync) transfer.Status = transfer.Status with { State = EngineDownloadState.Paused, DownloadSpeed = 0, Connections = 0 }; outcome = EngineDownloadEventKind.Paused; }
        catch (Exception ex) { lock (_sync) transfer.Status = transfer.Status with { State = EngineDownloadState.Error, DownloadSpeed = 0, Connections = 0, ErrorMessage = ex is MediaHelperException ? ex.Message : "The media helper could not complete this transfer." }; }
        finally {
            lock (_sync) { transfer.Cancellation?.Dispose(); transfer.Cancellation = null; transfer.Operation = null; Pump(); }
            DownloadEvent?.Invoke(this, new(transfer.Handle, outcome));
        }
    }
    private string[] BaseArguments() => ["--ignore-config", "--config-locations", "-", "--no-plugin-dirs", "--no-remote-components", "--no-playlist", "--no-allow-unplayable-formats", "--downloader", "native", "--proxy", "", "--quiet", "--no-warnings",
        "--ffmpeg-location", string.IsNullOrWhiteSpace(settings.FfmpegPath) ? Path.Combine(Path.GetDirectoryName(settings.YtDlpPath)!, ".colibri-no-ffmpeg") : settings.FfmpegPath];
    private void RequireEnabled() { if (!settings.MediaEnabled) throw new MediaHelperException("Optional media tools are disabled."); }
    private static string RequireExecutable(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path) ? Path.GetFullPath(path) : throw new MediaHelperException("Configure an existing absolute media-tool executable path.");
    private static void ValidateSource(Uri uri) { if (uri.Scheme is not ("http" or "https") || !UrlPolicy.TryValidate(uri.AbsoluteUri, out _, out string? _)) throw new MediaHelperException("Enter an HTTP or HTTPS media URL."); }
    private static LinkContext Context(DownloadRequest request) => new() { Cookies = request.Cookies, Headers = request.Headers, Referrer = request.Referrer, UserAgent = request.UserAgent };
    internal static string BuildConfiguration(Uri source, LinkContext context)
    {
        static string Quote(string value) {
            if (value.Length > 8192 || value.Any(char.IsControl)) throw new MediaHelperException("Invalid media request context.");
            return "'" + value.Replace("'", "'\\''") + "'";
        }
        var text = new StringBuilder();
        foreach (var (name, value) in context.Headers) {
            if (!HttpHeaders.IsValidName(name) || !HttpHeaders.IsValidValue(value)) throw new MediaHelperException("Invalid media request headers.");
            text.Append("--add-headers ").Append(Quote(name + ":" + value)).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(context.UserAgent)) text.Append("--user-agent ").Append(Quote(context.UserAgent)).Append('\n');
        if (Uri.TryCreate(context.Referrer, UriKind.Absolute, out var referrer) && referrer.Scheme is "http" or "https") text.Append("--referer ").Append(Quote(referrer.GetLeftPart(UriPartial.Authority) + "/")).Append('\n');
        text.Append("-- ").Append(Quote(source.AbsoluteUri)).Append('\n');
        return text.ToString();
    }
    private static async Task<string> ReadLicenseAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path); using var buffer = new MemoryStream(); var bytes = new byte[4096]; int read;
        while ((read = await file.ReadAsync(bytes, ct)) > 0) { if (buffer.Length + read > 256 * 1024) throw new MediaHelperException("The configured license exceeds its supported limit."); await buffer.WriteAsync(bytes.AsMemory(0, read), ct); }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private static async Task<string> CaptureAsync(string executable, IReadOnlyList<string> arguments, string? input, int maxCharacters, TimeSpan timeout, CancellationToken ct)
    {
        var text = new StringBuilder();
        await RunAsync(executable, arguments, input, line => { if (text.Length + line.Length + 1 > maxCharacters) throw new MediaHelperException("Media helper output exceeds its supported limit."); text.AppendLine(line); }, timeout, ct);
        return text.ToString();
    }
    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, string? input, Action<string> output, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); linked.CancelAfter(timeout);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(k => k.EndsWith("_proxy", StringComparison.OrdinalIgnoreCase) || k.StartsWith("YTDLP", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        using var process = new Process { StartInfo = start };
        try {
            if (!process.Start()) throw new MediaHelperException("The media tool could not start.");
            using var registration = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            async Task Drain(StreamReader reader, Action<string> line) {
                try {
                var buffer = new char[4096]; var pending = new StringBuilder(); int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), linked.Token)) > 0) {
                    for (var i = 0; i < read; i++) {
                        if (buffer[i] == '\n') { line(pending.ToString().TrimEnd('\r')); pending.Clear(); }
                        else { if (pending.Length >= MediaMetadataParser.MaxCharacters) throw new MediaHelperException("Media helper output line exceeds its supported limit."); pending.Append(buffer[i]); }
                    }
                }
                if (pending.Length > 0) line(pending.ToString());
                } catch { linked.Cancel(); throw; }
            }
            var stdout = Drain(process.StandardOutput, output);
            var stderr = Drain(process.StandardError, _ => { }); // Tool errors can contain credentials and signed URLs.
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), linked.Token);
            process.StandardInput.Close();
            var readers = Task.WhenAll(stdout, stderr);
            var exit = process.WaitForExitAsync(linked.Token);
            var first = await Task.WhenAny(readers, exit);
            if (first.IsFaulted) await first;
            await exit; await readers;
            linked.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new MediaHelperException("The media tool failed. Inspect the source and selected formats again.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MediaHelperException("The media tool exceeded its operation timeout."); }
        finally {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
        }
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Network;
using Colibri.Core.Torrents;

namespace Colibri.Engine.Aria2;

public sealed partial class Aria2Engine
{
    private readonly string _metadataRoot;
    private readonly ConcurrentDictionary<string, string> _metadataJobs = new();
    private readonly ConcurrentDictionary<string, TorrentMetadata> _torrentHandles = new();

    private static void ValidateTorrentPolicy(DownloadNetworkPolicy? policy)
    {
        Aria2NetworkPolicy.Validate(policy);
        if (policy?.Proxy is not null)
            throw new EngineOperationException("Torrent tracker, DHT and peer traffic cannot be isolated by aria2's HTTP proxy. Torrent operations with a configured proxy are blocked.");
    }

    private async Task<string> AddTorrentAsync(DownloadRequest request, string saveFolder, string fileName,
        string? handle, bool startPaused, CancellationToken ct)
    {
        var policy = request.NetworkPolicy ?? _options.NetworkPolicy ?? new DownloadNetworkPolicy();
        ValidateTorrentPolicy(policy);
        var torrent = request.Torrent!;
        var parsed = TorrentMetainfo.Parse(torrent.Metainfo);
        var selection = TorrentMetainfo.ValidateSelection(parsed.Metadata, torrent.SelectedFileIndices);
        torrent.SeedOptions.Validate();
        var gid = handle ?? Aria2AddOptions.NewGid();
        var options = Aria2AddOptions.Build(request, saveFolder, fileName, gid, _options.ConnectionsPerServer);
        Aria2NetworkPolicy.Apply(options, policy);
        options.Remove("out"); // addTorrent ignores out. Every indexed output is explicitly mapped instead.
        options["select-file"] = string.Join(',', selection);
        options["index-out"] = new JsonArray(parsed.Metadata.Files.Select(file =>
            JsonValue.Create($"{file.Index}={fileName}/{TorrentMetainfo.ContentRelativePath(parsed.Metadata, file)}")).ToArray());
        options["seed-time"] = torrent.SeedOptions.Enabled ? torrent.SeedOptions.TimeLimitMinutes.ToString(CultureInfo.InvariantCulture) : "0";
        options["seed-ratio"] = torrent.SeedOptions.Ratio.ToString(CultureInfo.InvariantCulture);
        options["max-upload-limit"] = torrent.SeedOptions.UploadLimitBytesPerSecond.ToString(CultureInfo.InvariantCulture);
        options["bt-seed-unverified"] = "false";
        options["bt-save-metadata"] = "false";
        options["rpc-save-upload-metadata"] = "false";
        options["bt-remove-unselected-file"] = "false";
        options["bt-enable-lpd"] = "false";
        if (parsed.Metadata.IsPrivate) options["enable-peer-exchange"] = "false";
        options["pause"] = startPaused ? "true" : "false";
        await Task.Run(() => ValidateContentDestination(saveFolder, fileName, parsed.Metadata), ct);
        try { await Client.AddTorrentAsync(parsed.Metainfo, options, ct); }
        catch (Aria2RpcException)
        { throw new EngineOperationException("aria2 could not add the torrent. Check its metadata and network settings."); }
        _networkPolicies[gid] = policy;
        _torrentHandles[gid] = parsed.Metadata;
        return gid;
    }

    public async Task<TorrentMetadataResult> PreviewMagnetAsync(string magnet, CancellationToken ct)
    {
        ValidateTorrentPolicy(_settings().Options.NetworkPolicy);
        var source = MagnetLink.Parse(magnet);
        if (!source.HasHttpTrackers)
            throw new EngineOperationException("Magnet metadata discovery requires an HTTP or HTTPS tracker. DHT and UDP tracker discovery are disabled; import local torrent metadata or supply a supported tracker.");
        var gid = Aria2AddOptions.NewGid();
        var directory = Path.Combine(_metadataRoot, Guid.NewGuid().ToString("N"));
        await Task.Run(() =>
        {
            RejectReparseAncestors(directory);
            Directory.CreateDirectory(directory);
            RejectReparseAncestors(directory);
        }, ct);
        _metadataJobs[gid] = directory;
        var attempted = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var options = new JsonObject
            {
                ["gid"] = gid, ["dir"] = directory, ["bt-metadata-only"] = "true",
                ["bt-save-metadata"] = "true", ["bt-load-saved-metadata"] = "false",
                ["pause-metadata"] = "true", ["seed-time"] = "0", ["pause"] = "false",
                ["rpc-save-upload-metadata"] = "false", ["bt-enable-lpd"] = "false",
                ["max-upload-limit"] = "65536",
            };
            Aria2NetworkPolicy.Apply(options, new());
            attempted = true;
            try { await Client.AddUriAsync([source.Uri], options, timeout.Token); }
            catch (Aria2RpcException) { throw new EngineOperationException("aria2 could not start torrent metadata discovery."); }
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var state = await ReadStateAsync(Client, gid, timeout.Token);
                if (state is EngineDownloadState.Error or EngineDownloadState.Removed or null)
                    throw new EngineOperationException("Torrent metadata discovery failed.");
                if (state == EngineDownloadState.Complete)
                {
                    var path = Path.Combine(directory, source.InfoHash + ".torrent");
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length is 0 or > TorrentMetainfo.MaxMetainfoBytes) throw new FormatException("Oversized torrent metadata.");
                    var bytes = new byte[checked((int)stream.Length)];
                    await stream.ReadExactlyAsync(bytes, timeout.Token);
                    var result = TorrentMetainfo.Parse(bytes);
                    if (result.Metadata.InfoHash != source.InfoHash) throw new FormatException("Torrent metadata does not match the magnet info hash.");
                    return result;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Torrent metadata discovery timed out."); }
        finally
        {
            try
            {
                if (attempted)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await RemoveAsync(gid, cleanup.Token); }
                    catch { await StopAsync(CancellationToken.None); }
                }
                await Task.Run(() => DeleteMetadataDirectory(directory));
            }
            finally { _metadataJobs.TryRemove(gid, out _); }
        }
    }

    public async Task StopSeedingAsync(string handle, CancellationToken ct)
    {
        var status = await GetStatusAsync(handle, ct);
        if (!_torrentHandles.ContainsKey(handle) || status is null || (!status.IsSeeding
            && status.State != EngineDownloadState.Complete
            && status.State != EngineDownloadState.Paused))
            throw new EngineOperationException("This torrent is not seeding.");
        // Reconstructed paused jobs can report zero lengths before aria2 checks their files.
        // This operation only stops/removes engine state; the manager owns completion proof.
        await RemoveAsync(handle, ct); // Removes engine state only; content remains on disk.
    }

    private static void ValidateContentDestination(string folder, string fileName, TorrentMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName)
            || fileName != Colibri.Core.Services.FileNameSanitizer.Sanitize(fileName)
            || fileName is "." or ".." || fileName.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
            throw new EngineOperationException("Invalid torrent content directory.");
        var root = Path.GetFullPath(Path.Combine(folder, fileName));
        RejectReparseAncestors(root);
        foreach (var file in metadata.Files)
        {
            var path = Path.GetFullPath(Path.Combine(root, TorrentMetainfo.ContentRelativePath(metadata, file)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new EngineOperationException("Torrent output is outside its content directory.");
            RejectReparseAncestors(path);
        }
        Directory.CreateDirectory(root);
        RejectReparseAncestors(root);
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new EngineOperationException("Torrent output through symbolic links or junctions is blocked.");
    }

    private void DeleteMetadataDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(full) != _metadataRoot || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new EngineOperationException("Unexpected torrent metadata cleanup path.");
        if (!Directory.Exists(full)) return;
        RejectReparseAncestors(full);
        // Metadata-only mode creates files at this owned directory's root. Never recursively
        // delete unexpected subdirectories or user download files.
        foreach (var file in Directory.EnumerateFiles(full)) File.Delete(file);
        Directory.Delete(full, recursive: false);
    }
}

using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Network;
using Colibri.Core.Torrents;

namespace Colibri.Core.Services;

public sealed partial class DownloadManager
{
    public async Task<TorrentMetadataResult> LoadTorrentFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > TorrentMetainfo.MaxMetainfoBytes)
            throw new ArgumentException("Choose a torrent file smaller than 2 MiB.");
        var bytes = new byte[TorrentMetainfo.MaxMetainfoBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
        }
        if (length > TorrentMetainfo.MaxMetainfoBytes) throw new ArgumentException("The torrent file is too large.");
        return TorrentMetainfo.Parse(bytes.AsSpan(0, length).ToArray());
    }

    public async Task<TorrentMetadataResult> FetchMagnetMetadataAsync(string magnet, CancellationToken ct)
    {
        _ = MagnetLink.Parse(magnet);
        EnsureTorrentNetworkAllowed(_settings.DefaultNetworkPolicy);
        var engine = _engines.OfType<ITorrentEngine>().FirstOrDefault(e => e.State == EngineState.Running)
            ?? throw new EngineOperationException("The torrent engine is unavailable.");
        // The engine owns this temporary metadata lease, including cleanup after cancellation.
        return await engine.PreviewMagnetAsync(magnet, ct);
    }

    public Task<DownloadItem> AddTorrentAsync(TorrentMetadataResult preview, IReadOnlyList<int> selectedIndices,
        string folder, Guid queueId, TorrentSeedOptions seedOptions, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Choose a full destination path.");
        var parsed = TorrentMetainfo.Parse(preview.Metainfo);
        var selected = TorrentMetainfo.ValidateSelection(parsed.Metadata, selectedIndices);
        ValidateSeedOptions(seedOptions);
        EnsureTorrentNetworkAllowed(_settings.DefaultNetworkPolicy);
        return RunLockedAsync(async changes =>
        {
            if (!_queues.ContainsKey(queueId)) throw new ArgumentException("Unknown queue.");
            var request = new DownloadRequest
            {
                Uri = new Uri("magnet:?xt=urn:btih:" + parsed.Metadata.InfoHash),
                SuggestedFileName = parsed.Metadata.Name,
                Size = parsed.Metadata.Files.Where(file => selected.Contains(file.Index)).Sum(file => file.Length),
                NetworkPolicy = _settings.DefaultNetworkPolicy ?? new DownloadNetworkPolicy(),
                Torrent = new TorrentDownload(parsed.Metainfo, selected, seedOptions)
            };
            return (await AddOneAsync(request, parsed.Metadata.Name, folder, changes, ct, queueId)).Clone();
        }, ct);
    }

    /// <summary>Selection changes are committed while stopped, then reconstructed paused under the same handle.</summary>
    public Task UpdateTorrentAsync(Guid id, IReadOnlyList<int> selectedIndices, TorrentSeedOptions seedOptions, CancellationToken ct) =>
        RunLockedAsync(async changes =>
        {
            if (!_items.TryGetValue(id, out var item) || item.Torrent is not { } torrent || item.State == DownloadState.Completed)
                throw new EngineOperationException("The torrent is unavailable.");
            var parsed = TorrentMetainfo.Parse(torrent.Metainfo);
            var selected = TorrentMetainfo.ValidateSelection(parsed.Metadata, selectedIndices);
            var selectionChanged = !torrent.SelectedFileIndices.SequenceEqual(selected);
            ValidateSeedOptions(seedOptions);
            EnsureTorrentNetworkAllowed(item.NetworkPolicy);
            await StopTransferForCancellationAsync(item, changes);
            var engine = EngineOf(item);
            if (engine is { State: EngineState.Running } && item.EngineHandle is { } handle)
                await engine.RemoveAsync(handle, ct);
            item.Torrent = new(parsed.Metainfo, selected, seedOptions);
            item.TotalBytes = parsed.Metadata.Files.Where(file => selected.Contains(file.Index)).Sum(file => file.Length);
            if (selectionChanged)
            {
                // Cached progress belongs to the previous selection. Native integrity checking
                // determines which bytes of the new selection are actually present.
                item.CompletedBytes = 0;
                item.CompletedAt = null;
                item.SeedStartedAt = null;
                item.Bitfield = null;
            }
            else item.CompletedBytes = Math.Min(item.CompletedBytes, item.TotalBytes.Value);
            item.IsSeeding = false;
            item.UploadSpeed = 0;
            item.QueueHeld = false;
            await SaveAsync(item);
            changes.Update(item);
            if (_unsaved.Contains(item.Id)) throw new IOException("The torrent selection could not be saved. The download remains paused.");
            if (engine is { State: EngineState.Running })
                await AddToEngineAsync(engine, item, startPaused: true, changes, ct);
            return true;
        }, ct);

    public Task StopSeedingAsync(Guid id, CancellationToken ct) => RunLockedAsync(async changes =>
    {
        if (!_items.TryGetValue(id, out var item) || item.Torrent is null || item.TotalBytes is not { } total || item.CompletedBytes < total)
            throw new EngineOperationException("The torrent has not finished downloading.");
        await StopSeedingItemAsync(item, changes, ct);
        return true;
    }, ct);

    private async Task StopSeedingItemAsync(DownloadItem item, Changes changes, CancellationToken ct)
    {
        await StopTransferForCancellationAsync(item, changes);
        if (EngineOf(item) is ITorrentEngine { State: EngineState.Running } engine && item.EngineHandle is { } handle)
            await engine.StopSeedingAsync(handle, ct);
        TrySetState(item, DownloadState.Completed);
        item.CompletedAt ??= _time.GetUtcNow();
        item.IsSeeding = false;
        item.UploadSpeed = 0;
        item.Headers.Clear();
        changes.Update(item);
        await SaveAsync(item);
        if (_unsaved.Contains(item.Id)) throw new IOException("The seeding stop could not be saved.");
    }

    private async Task StopExpiredSeedsAsync(Changes changes, CancellationToken ct)
    {
        foreach (var item in _items.Values.Where(item => item.Torrent is not null && item.State is DownloadState.Active or DownloadState.Paused).ToArray())
        {
            if (item.Torrent?.SeedOptions is not { Enabled: true } options || item.SeedStartedAt is not { } started
                || item.TotalBytes is not { } total || item.CompletedBytes < total) continue;
            if (_time.GetUtcNow() - started >= TimeSpan.FromMinutes(options.TimeLimitMinutes)
                || (total > 0 && (double)item.UploadedBytes / total >= options.Ratio))
                await StopSeedingItemAsync(item, changes, ct);
        }
    }

    private TorrentDownload? RemainingTorrentBudget(DownloadItem item)
    {
        if (item.Torrent is not { } torrent || !torrent.SeedOptions.Enabled) return item.Torrent;
        var options = torrent.SeedOptions;
        var minutes = options.TimeLimitMinutes - (item.SeedStartedAt is { } started
            ? Math.Max(0, (_time.GetUtcNow() - started).TotalMinutes) : 0);
        var ratio = options.Ratio - (item.TotalBytes is > 0 ? (double)item.UploadedBytes / item.TotalBytes.Value : 0);
        return torrent with { SeedOptions = minutes <= 0 || ratio <= 0 ? options with { Enabled = false }
            : options with { TimeLimitMinutes = (int)Math.Ceiling(minutes), Ratio = ratio } };
    }

    private void DeleteTorrentFiles(DownloadItem item)
    {
        if (item.Torrent is not { } torrent) return;
        var metadata = TorrentMetainfo.Parse(torrent.Metainfo).Metadata;
        var root = Path.GetFullPath(Path.Combine(item.SaveFolder, item.FileName));
        // Only declared torrent paths are owned. Unknown files and replaced/symlinked folders survive.
        foreach (var file in metadata.Files)
        {
            var relative = TorrentMetainfo.ContentRelativePath(metadata, file);
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var safe = true;
            for (var parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            {
                if ((Directory.Exists(parent) || File.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                { safe = false; break; }
            }
            if (safe) DeleteFile(path);
        }
        var control = Path.Combine(item.SaveFolder, item.FileName + ".aria2");
        for (var parent = control; parent is not null; parent = Path.GetDirectoryName(parent))
            if ((Directory.Exists(parent) || File.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return;
        DeleteFile(control);
    }

    private static void EnsureTorrentNetworkAllowed(DownloadNetworkPolicy? policy)
    {
        if (policy?.Proxy is not null || policy?.RequiredInterfaceId is not null)
            throw new EngineOperationException("Torrent peer traffic cannot enforce the selected proxy or adapter policy. No metadata or peer transfer was started.");
    }

    private static void ValidateSeedOptions(TorrentSeedOptions options) => options.Validate();
}

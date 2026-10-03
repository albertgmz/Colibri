using System.Text.Json.Nodes;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Colibri.Engine.Aria2;

/// <summary>
/// <see cref="IDownloadEngine"/> backed by aria2c, run as a hidden child process and controlled only
/// through JSON-RPC over WebSocket. Safe to call from many threads.
/// </summary>
/// <remarks>
/// Handles are aria2 GIDs chosen by Colibri. Removing a download leaves both the file and its
/// <c>.aria2</c> control file on disk; read <see cref="EngineDownloadStatus.FilePath"/> first if
/// they should be deleted. After aria2 restarts (state goes Restarting -> Running) callers should
/// re-read all downloads with <see cref="GetAllAsync"/>.
/// </remarks>
public sealed class Aria2Engine : IDownloadEngine, ILegacyCredentialCleanup, IDisposable
{
    // tellWaiting/tellStopped return pages. One page of 1000 covers any realistic queue; downloads beyond
    // it are not reported by GetAllAsync.
    private const int ListPageSize = 1000;

    private static readonly string[] SupportedSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeFtp];
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(5);

    private readonly Aria2Process? _process;
    private readonly Aria2RpcClient? _testClient;
    private readonly string? _legacySessionPath;
    private readonly Func<Aria2Settings> _settings;
    private volatile EngineOptions _options;

    public Aria2Engine(IAria2Locator locator, IAppPaths paths, ILogger<Aria2Engine> logger, Func<Aria2Settings> settings)
    {
        _settings = settings;
        _legacySessionPath = paths.Aria2SessionPath;
        _options = settings().Options;
        _process = new Aria2Process(locator, paths, logger, () => _options);
        _process.StateChanged += (_, state) => StateChanged?.Invoke(this, state);
        _process.DownloadEvent += (_, e) => DownloadEvent?.Invoke(this, e);
    }

    /// <summary>Test constructor: talks to an already connected client instead of launching aria2.</summary>
    internal Aria2Engine(Aria2RpcClient client, EngineOptions options)
    {
        _testClient = client;
        _options = options;
        _settings = () => new Aria2Settings(null, _options);
        client.Notification += (_, e) => DownloadEvent?.Invoke(this, e);
    }

    public string Id => "aria2";

    public EngineState State => _process?.State ?? EngineState.Running;

    public event EventHandler<EngineDownloadEvent>? DownloadEvent;

    public event EventHandler<EngineState>? StateChanged;

    /// <summary>Process id of the running aria2c, or null. For the smoke test.</summary>
    internal int? Aria2ProcessId => _process?.ProcessId;

    private Aria2RpcClient Client =>
        (_process is null ? _testClient : _process.Client)
        ?? throw new EngineOperationException("The aria2 engine is not running.");

    public bool CanHandle(DownloadRequest request) =>
        SupportedSchemes.Contains(request.Uri.Scheme, StringComparer.OrdinalIgnoreCase);

    public Task StartAsync(CancellationToken ct)
    {
        var settings = _settings();
        _options = settings.Options;
        return _process?.StartAsync(settings.Aria2Path, ct) ?? Task.CompletedTask;
    }

    /// <remarks>
    /// <paramref name="ct"/> is not used: stopping always runs to the end (each step has its own time
    /// limit), because a half-done stop would leave aria2 running unsupervised.
    /// </remarks>
    public Task StopAsync(CancellationToken ct) => _process?.StopAsync() ?? Task.CompletedTask;

    public async Task ApplyOptionsAsync(EngineOptions options, CancellationToken ct)
    {
        _options = options;
        if (State != EngineState.Running)
        {
            return; // Used on the next start.
        }

        var globalOptions = new JsonObject();
        foreach (var (name, value) in Aria2Arguments.GlobalOptions(options))
        {
            globalOptions[name] = value;
        }

        await Refusable(() => Client.ChangeGlobalOptionAsync(globalOptions, ct), "change its options");
    }

    public string CreateHandle() => Aria2AddOptions.NewGid();

    public async Task<string> AddAsync(
        DownloadRequest request, string saveFolder, string fileName, string? handle, bool startPaused, CancellationToken ct)
    {
        if (!CanHandle(request))
        {
            throw new ArgumentException($"aria2 cannot download '{request.Uri.Scheme}' URLs.", nameof(request));
        }

        var gid = handle ?? Aria2AddOptions.NewGid();
        var options = Aria2AddOptions.Build(request, saveFolder, fileName, gid, _options.ConnectionsPerServer);
        if (startPaused)
        {
            // aria2's "pause" option adds the download in the paused state.
            options["pause"] = "true";
        }

        await Refusable(() => Client.AddUriAsync([Aria2AddOptions.ToAria2Url(request.Uri)], options, ct), "add the download");
        return gid;
    }

    public Task PauseAsync(string handle, CancellationToken ct) =>
        Refusable(() => Client.ForcePauseAsync(handle, ct), $"pause download {handle}");

    public Task ResumeAsync(string handle, CancellationToken ct) =>
        Refusable(() => Client.UnpauseAsync(handle, ct), $"resume download {handle}");

    public async Task RemoveAsync(string handle, CancellationToken ct)
    {
        var client = Client;
        var state = await ReadStateAsync(client, handle, ct);
        if (state is null)
        {
            return; // Already gone.
        }

        if (IsUnfinished(state.Value))
        {
            try
            {
                await client.ForceRemoveAsync(handle, ct);
            }
            catch (Aria2RpcException ex)
            {
                // The download may have finished between the two calls; then only its result is left to clear.
                var now = await ReadStateAsync(client, handle, ct);
                if (now is null)
                {
                    return;
                }

                if (IsUnfinished(now.Value))
                {
                    throw new EngineOperationException($"aria2 could not remove download {handle}: {ex.Message}", ex);
                }
            }

            await WaitUntilStoppedAsync(client, handle, ct);
        }

        // aria2 keeps stopped downloads (complete, error, removed) in memory until their result is removed.
        await Refusable(() => client.RemoveDownloadResultAsync(handle, ct), $"forget download {handle}");
    }

    public async Task<EngineDownloadStatus?> GetStatusAsync(string handle, CancellationToken ct)
    {
        try
        {
            return Aria2Status.Parse(await Client.TellStatusAsync(handle, Aria2Status.Keys, ct));
        }
        catch (Aria2RpcException ex) when (ex.IsGidNotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<EngineDownloadStatus>> GetAllAsync(CancellationToken ct)
    {
        var all = await Client.TellAllAsync(ListPageSize, Aria2Status.Keys, ct);
        return all.Select(Aria2Status.Parse).ToList();
    }

    public async Task<EngineGlobalStats> GetGlobalStatsAsync(CancellationToken ct)
    {
        var stat = await Client.GetGlobalStatAsync(ct);
        return new EngineGlobalStats(
            Aria2Status.ParseLong(stat["downloadSpeed"]),
            (int)Aria2Status.ParseLong(stat["numActive"]),
            (int)Aria2Status.ParseLong(stat["numWaiting"]));
    }

    public void Dispose() => _process?.Dispose();

    public async Task<EngineDownloadDetails?> GetDetailsAsync(string handle, CancellationToken ct)
    {
        JsonArray servers;
        try
        {
            servers = await Client.CallAsync("aria2.getServers", [handle], ct) as JsonArray
                ?? throw new FormatException("aria2 returned invalid server details.");
        }
        catch (Aria2RpcException)
        {
            // getServers requires an active transfer. Paused/waiting downloads still have
            // editable options; verify their actual state before treating servers as absent.
            var state = await ReadStateAsync(Client, handle, ct);
            if (state is null or EngineDownloadState.Active) throw;
            servers = [];
        }
        var rows = new List<DownloadServer>();
        foreach (var file in servers.OfType<JsonObject>())
        {
            var index = (int)Aria2Status.ParseLong(file["index"]);
            if (file["servers"] is not JsonArray list) continue;
            foreach (var server in list.OfType<JsonObject>())
            {
                // currentUri is the URI actually serving bytes, including a reported redirect.
                var current = server["currentUri"]?.ToString();
                var uri = current ?? server["uri"]?.ToString();
                rows.Add(new(index, Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : string.Empty,
                    current, Aria2Status.ParseLong(server["downloadSpeed"])));
            }
        }
        var options = await Client.CallAsync("aria2.getOption", [handle], ct) as JsonObject
            ?? throw new FormatException("aria2 returned invalid transfer options.");
        return new(rows, new(Aria2Status.ParseLong(options["max-download-limit"]),
            (int)Aria2Status.ParseLong(options["max-connection-per-server"])));
    }

    public Task CleanupAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_process?.ProcessId is not null)
            throw new InvalidOperationException("Legacy credentials must be cleaned before aria2 starts.");
        if (_legacySessionPath is { } path)
        {
            // Fixed legacy paths only. Never enumerate or touch download/control files.
            // The caller establishes that protected DB initialization succeeded first.
            DeleteLegacyFile(path);
            ct.ThrowIfCancellationRequested();
            DeleteLegacyFile(path + ".tmp");
        }
        return Task.CompletedTask;
    }

    private static void DeleteLegacyFile(string path)
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { } // A missing legacy folder has nothing to clean.
    }

    public Task ApplyDownloadOptionsAsync(string handle, DownloadTransferOptions options, CancellationToken ct)
    {
        if (options.SpeedLimitBytesPerSecond < 0 || options.ConnectionsPerServer is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(options));
        var values = new JsonObject
        {
            ["max-download-limit"] = options.SpeedLimitBytesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["max-connection-per-server"] = options.ConnectionsPerServer.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["split"] = options.ConnectionsPerServer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return Refusable(() => Client.CallAsync("aria2.changeOption", [handle, values], ct), "change download options");
    }

    /// <summary>The download's state, or null when aria2 does not know the GID.</summary>
    private static async Task<EngineDownloadState?> ReadStateAsync(Aria2RpcClient client, string handle, CancellationToken ct)
    {
        try
        {
            return Aria2Status.ParseState((await client.TellStatusAsync(handle, ["status"], ct))["status"]?.ToString());
        }
        catch (Aria2RpcException ex) when (ex.IsGidNotFound)
        {
            return null;
        }
    }

    private static bool IsUnfinished(EngineDownloadState state) =>
        state is EngineDownloadState.Active or EngineDownloadState.Waiting or EngineDownloadState.Paused;

    // forceRemove returns before aria2 has actually stopped the download; its result can only be
    // removed once the status has changed to "removed".
    private static async Task WaitUntilStoppedAsync(Aria2RpcClient client, string handle, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RemoveTimeout);
        try
        {
            while (true)
            {
                if (await ReadStateAsync(client, handle, timeout.Token) is not { } state || !IsUnfinished(state))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"aria2 did not stop download {handle} within {RemoveTimeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// Runs an aria2 call and turns an aria2 refusal (wrong state, unknown GID, bad option) into
    /// <see cref="EngineOperationException"/>, which callers can handle without knowing about aria2.
    /// </summary>
    private static async Task Refusable(Func<Task> call, string action)
    {
        try
        {
            await call();
        }
        catch (Aria2RpcException ex)
        {
            throw new EngineOperationException($"aria2 could not {action}: {ex.Message}", ex);
        }
    }
}

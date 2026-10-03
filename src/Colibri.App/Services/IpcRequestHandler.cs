using Avalonia.Threading;
using Colibri.App.ViewModels;
using Colibri.Core.Ipc;
using Colibri.Core.Settings;
using Colibri.Core.Services;
using System.Collections.Concurrent;

namespace Colibri.App.Services;

/// <summary>
/// Answers requests from the local pipe: <c>activate</c> from a second Colibri process, and <c>add</c>,
/// <c>ping</c> and <c>config</c> from the browser's native-messaging host. Requests arrive on a background
/// thread; window work is done on the UI thread.
/// </summary>
public sealed class IpcRequestHandler
{
    private const string ExitingError = "Colibri is exiting.";

    private readonly MainWindowViewModel _viewModel;
    private readonly IDialogService _dialogs;
    private readonly Action _showMainWindow;
    private readonly AppSettings _settings;
    private readonly ISettingsStore? _store;
    private readonly ConcurrentDictionary<string, CaptureSession> _captures = new();

    public IpcRequestHandler(MainWindowViewModel viewModel, IDialogService dialogs, Action showMainWindow, AppSettings settings, ISettingsStore? store = null)
    {
        _viewModel = viewModel;
        _dialogs = dialogs;
        _showMainWindow = showMainWindow;
        _settings = settings;
        _store = store;
    }

    /// <summary>Handles a request that <see cref="IpcProtocol"/> has already validated.</summary>
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        switch (request)
        {
            case HelloRequest hello:
                return hello.ProtocolVersion == BrowserProtocol.Version
                    ? new IpcResponse(true, Config: CaptureConfigFrom(_settings), ProtocolVersion: BrowserProtocol.Version,
                        AppVersion: typeof(IpcRequestHandler).Assembly.GetName().Version?.ToString(), Capabilities: BrowserProtocol.Capabilities)
                    : IpcResponse.Failure("Unsupported protocol version.");
            case OpenRequest:
                if (ct.IsCancellationRequested) return IpcResponse.Failure(ExitingError);
                await OnUiThreadAsync(_showMainWindow);
                return IpcResponse.Success;
            case CaptureStatusRequest status:
                return _captures.TryGetValue(status.CaptureId, out var capture)
                    ? new IpcResponse(true, State: capture.State, CaptureId: capture.Id)
                    : IpcResponse.Failure("Unknown capture.");
            case CaptureCancelRequest cancel:
                if (_captures.TryGetValue(cancel.CaptureId, out var canceled))
                {
                    canceled.Reject("browser");
                    // Pending is authoritative while rollback is in flight or could not be confirmed.
                    // Reply promptly so native response deadlines cannot turn that into browser release.
                }
                return new IpcResponse(true, State: canceled?.State ?? "browser", CaptureId: cancel.CaptureId);
            case SettingsUpdateRequest update:
                if (_store is null) return IpcResponse.Failure("Settings storage unavailable.");
                await OnUiThreadAsync(() =>
                {
                    if (update.Patch.Enabled is { } enabled) _settings.BrowserCaptureEnabled = enabled;
                    if (update.Patch.CapturePrivate is { } capturePrivate) _settings.BrowserCapturePrivate = capturePrivate;
                    if (update.Patch.ExcludedSites is { } sites) _settings.BrowserExcludedSites = sites.Select(s => s.ToLowerInvariant()).Distinct().ToList();
                    if (update.Patch.BypassModifier is { } modifier) _settings.BrowserBypassModifier = modifier;
                });
                await _store.SaveAsync(_settings, ct);
                return new IpcResponse(true, Config: CaptureConfigFrom(_settings));
            case BulkAddRequest bulk:
                if (ct.IsCancellationRequested) return IpcResponse.Failure(ExitingError);
                var bulkCapture = NewCapture();
                if (bulkCapture is null) return IpcResponse.Failure("Too many pending captures.");
                await OnUiThreadAsync(() => _dialogs.ShowBulkAdd(_viewModel.CreateBulkAdd(bulk.Links, bulkCapture)));
                return new IpcResponse(true, State: "pending", CaptureId: bulkCapture.Id);
            case ActivateRequest activate:
                // ct is cancelled when Colibri starts exiting (checked on the UI thread, where the exit runs).
                // The refusal tells the new Colibri process to wait and take over instead of exiting too.
                // "--minimized" (an autostart while Colibri already runs) changes nothing on screen.
                var accepted = false;
                await OnUiThreadAsync(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        if (!WindowBehavior.HasMinimizedArgument(activate.Args))
                        {
                            _showMainWindow();
                        }

                        accepted = true;
                    }
                });
                return accepted ? IpcResponse.Success : IpcResponse.Failure(ExitingError);

            case AddRequest add:
                if (ct.IsCancellationRequested) return IpcResponse.Failure(ExitingError);
                var offer = NewCapture();
                if (offer is null) return IpcResponse.Failure("Too many pending captures.");
                await OnUiThreadAsync(() =>
                {
                    // Captured headers belong to the final request, never replay them at the original host.
                    var view = _viewModel.CreateAddUrl(add.Context, add.FinalUrl ?? add.Url);
                    view.AttachCapture(offer);
                    offer.Token.Register(() => Dispatcher.UIThread.Post(view.DismissCapture));
                    _dialogs.ShowAddUrl(view);
                });
                return new IpcResponse(true, State: "pending", CaptureId: offer.Id);

            case PingRequest:
                return IpcResponse.Success;

            case ConfigRequest:
                return new IpcResponse(true, Config: CaptureConfigFrom(_settings));

            default:
                return IpcResponse.Failure("Unsupported request.");
        }
    }

    /// <summary>
    /// The capture rules within the limits the host accepts (a hand-edited settings file can hold anything):
    /// extensions of letters and digits only, lower case, at most <see cref="IpcProtocol.MaxCaptureExtensions"/>.
    /// The settings page replaces the list rather than changing it, so reading it here is safe.
    /// </summary>
    internal static CaptureConfig CaptureConfigFrom(AppSettings settings) => new(
        settings.BrowserCaptureExtensions
            .Where(e => e.Length is > 0 and <= IpcProtocol.MaxCaptureExtensionLength && e.All(char.IsAsciiLetterOrDigit))
            .Select(e => e.ToLowerInvariant())
            .Distinct()
            .Take(IpcProtocol.MaxCaptureExtensions)
            .ToList(),
        Math.Max(0, settings.BrowserCaptureMinSizeKiB), settings.BrowserCaptureEnabled,
        settings.BrowserExcludedSites ?? [], settings.BrowserCapturePrivate, settings.BrowserBypassModifier,
        settings.Theme.ToString().ToLowerInvariant(), settings.AccentColor);

    private CaptureSession? NewCapture()
    {
        // Retain bounded terminal states long enough for the extension to poll them.
        if (_captures.Count >= 256)
            foreach (var pair in _captures.Where(pair => pair.Value.State != "pending").Take(128))
                if (_captures.TryRemove(pair.Key, out var old)) old.Dispose();
        if (_captures.Count >= 256) return null;
        var capture = new CaptureSession(TimeSpan.FromMinutes(5));
        _captures[capture.Id] = capture;
        return capture;
    }

    private static async Task OnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(action);
        }
    }
}

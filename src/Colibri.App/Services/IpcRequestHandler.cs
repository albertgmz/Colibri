using Avalonia.Threading;
using Colibri.App.ViewModels;
using Colibri.Core.Ipc;
using Colibri.Core.Settings;

namespace Colibri.App.Services;

/// <summary>
/// Answers requests from the local pipe: <c>activate</c> from a second Colibri process, and <c>add</c>,
/// <c>ping</c> and <c>config</c> from the browser's native-messaging host. Requests arrive on a background
/// thread; window work is done on the UI thread.
/// </summary>
/// <remarks>
/// An <c>add</c> is answered "ok" as soon as the Add URL window is shown, before the user confirms it.
/// The browser cancels its own download on "ok"; if the user then cancels Colibri's window, the download
/// is dropped. That is intended: the user chose to cancel.
/// </remarks>
public sealed class IpcRequestHandler
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IDialogService _dialogs;
    private readonly Action _showMainWindow;
    private readonly AppSettings _settings;

    public IpcRequestHandler(MainWindowViewModel viewModel, IDialogService dialogs, Action showMainWindow, AppSettings settings)
    {
        _viewModel = viewModel;
        _dialogs = dialogs;
        _showMainWindow = showMainWindow;
        _settings = settings;
    }

    /// <summary>Handles a request that <see cref="IpcProtocol"/> has already validated.</summary>
    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        switch (request)
        {
            case ActivateRequest activate:
                // "--minimized" (an autostart while Colibri already runs) changes nothing on screen.
                if (!WindowBehavior.HasMinimizedArgument(activate.Args))
                {
                    await OnUiThreadAsync(_showMainWindow);
                }

                return IpcResponse.Success;

            case AddRequest add:
                // ct is cancelled when Colibri starts exiting. Checked on the UI thread, where the exit runs:
                // a window shown then would be closed by the exit, and the browser, told "ok", would have
                // dropped its own download.
                var shown = false;
                await OnUiThreadAsync(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        _dialogs.ShowAddUrl(_viewModel.CreateAddUrl(add.Context, add.Url));
                        shown = true;
                    }
                });
                return shown ? IpcResponse.Success : IpcResponse.Failure("Colibri is exiting.");

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
        Math.Max(0, settings.BrowserCaptureMinSizeKiB));

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

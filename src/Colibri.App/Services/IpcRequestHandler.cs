using Avalonia.Threading;
using Colibri.App.ViewModels;
using Colibri.Core.Ipc;

namespace Colibri.App.Services;

/// <summary>
/// Answers requests from the local pipe: <c>activate</c> from a second Colibri process and <c>add</c> from
/// the browser. Requests arrive on a background thread; the work is done on the UI thread.
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

    public IpcRequestHandler(MainWindowViewModel viewModel, IDialogService dialogs, Action showMainWindow)
    {
        _viewModel = viewModel;
        _dialogs = dialogs;
        _showMainWindow = showMainWindow;
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
                await OnUiThreadAsync(() => _dialogs.ShowAddUrl(_viewModel.CreateAddUrl(add.Context, add.Url)));
                return IpcResponse.Success;

            default:
                return IpcResponse.Failure("Unsupported request.");
        }
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

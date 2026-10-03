using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Colibri.App.Resources;
using Colibri.App.ViewModels;
using Colibri.App.Views;
using Colibri.Core.Ipc;
using Colibri.Core.Platform;
using Colibri.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Colibri.App.Services;

/// <summary>
/// The desktop side of the app: shows, hides and restores the main window, owns the tray icon and the
/// local pipe server, keeps the taskbar progress current, and runs the orderly exit.
/// </summary>
/// <remarks>
/// The app uses <see cref="ShutdownMode.OnExplicitShutdown"/>: Colibri keeps running while its window is
/// hidden in the tray, and only <see cref="ExitAsync"/> ends it.
/// </remarks>
public sealed class DesktopShell
{
    private static readonly Uri IconUri = new("avares://Colibri/Assets/colibri.ico");

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindowViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly IWindowChrome _chrome;
    private readonly ITrayAvailability _trayAvailability;
    private readonly ITaskbarProgress _taskbar;
    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;

    private MainWindow? _window;
    private TrayIcon? _trayIcon;
    private LocalPipeServer? _pipeServer;
    private bool _trayAvailable;
    private WindowState _restoreState = WindowState.Normal;
    private double? _shownProgress;
    private bool _exiting;
    private bool _exitFinished;

    public DesktopShell(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel viewModel,
        AppSettings settings,
        IWindowChrome chrome,
        ITrayAvailability trayAvailability,
        ITaskbarProgress taskbar,
        IDialogService dialogs,
        ILogger<DesktopShell> logger)
    {
        _desktop = desktop;
        _viewModel = viewModel;
        _settings = settings;
        _chrome = chrome;
        _trayAvailability = trayAvailability;
        _taskbar = taskbar;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>
    /// Creates the main window and the tray icon, shows the window (or not, for <c>--minimized</c>) and
    /// starts the local pipe server. Call on the UI thread after the lifetime has started.
    /// </summary>
    public async Task StartAsync(bool minimizedArgument)
    {
        try
        {
            _trayAvailable = await _trayAvailability.IsAvailableAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not find out whether a tray icon can be shown; assuming not");
            _trayAvailable = false;
        }

        _viewModel.SetTrayAvailable(_trayAvailable);

        var window = new MainWindow { DataContext = _viewModel, Icon = LoadIcon() };
        _chrome.Apply(window);
        window.Closing += OnClosing;
        window.PropertyChanged += OnWindowPropertyChanged;
        _window = window;
        _desktop.MainWindow = window;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        if (_trayAvailable)
        {
            _trayIcon = CreateTrayIcon();
        }

        switch (WindowBehavior.OnStartup(minimizedArgument, _trayAvailable))
        {
            case StartupWindowMode.Shown:
                window.Show();
                break;
            case StartupWindowMode.Minimized:
                window.WindowState = WindowState.Minimized;
                window.Show();
                break;
            default:
                // Never shown, so the window raises no visibility change: slow polling is set here.
                _viewModel.SetWindowVisible(false);
                break;
        }

        var handler = new IpcRequestHandler(_viewModel, _dialogs, ShowMainWindow, _settings);
        _pipeServer = new LocalPipeServer(IpcProtocol.DefaultPipeName, handler.HandleAsync, _logger);
        _pipeServer.Start();
    }

    /// <summary>Shows, restores and brings the main window to the front.</summary>
    public void ShowMainWindow()
    {
        if (_window is not { } window || _exiting)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = _restoreState;
        }

        // Windows lets only the foreground app move its windows to the front; raised from the tray or
        // from another process, the window could stay behind. Topmost for a moment puts it on top.
        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
    }

    /// <summary>
    /// Ends Colibri: hides the window, removes the tray icon, stops the pipe server and the downloads (aria2
    /// saves its session), then shuts the app down. Safe to call more than once.
    /// </summary>
    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _window?.Hide();
        _trayIcon?.Dispose();
        _trayIcon = null;
        try
        {
            if (_pipeServer is not null)
            {
                await _pipeServer.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stopping the local pipe failed");
        }

        // Separately: the downloads must be stopped (aria2 saving its session) whatever happened above.
        try
        {
            await _viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stopping the downloads failed");
        }

        _exitFinished = true;
        _desktop.Shutdown();
    }

    private void HideToTray()
    {
        _window?.Hide();
    }

    private void ToggleMainWindow()
    {
        if (_window is { IsVisible: true } window && window.WindowState != WindowState.Minimized)
        {
            HideToTray();
        }
        else
        {
            ShowMainWindow();
        }
    }

    /// <summary>
    /// Closing the window hides it in the tray or exits, depending on the settings. Any close is cancelled
    /// first, so the exit can stop the downloads asynchronously; the app shuts down afterwards.
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitFinished)
        {
            return;
        }

        e.Cancel = true;

        // Only the user closing the window may hide it; an OS logout or shutdown always exits.
        if (!_exiting && e.CloseReason == WindowCloseReason.WindowClosing
            && WindowBehavior.OnClose(_trayAvailable, _settings.CloseToTray) == CloseAction.HideToTray)
        {
            HideToTray();
            return;
        }

        _ = ExitAsync();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.GetNewValue<bool>())
        {
            // Hiding removes the taskbar button and Windows forgets its progress; send it again.
            _shownProgress = null;
            UpdateTaskbar();
            return;
        }

        if (e.Property != Window.WindowStateProperty || _window is not { } window)
        {
            return;
        }

        var state = e.GetNewValue<WindowState>();
        if (state != WindowState.Minimized)
        {
            _restoreState = state;
        }
        else if (WindowBehavior.HideOnMinimize(_trayAvailable, _settings.MinimizeToTray))
        {
            // Hide after the minimize has finished, not in the middle of the state change.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    HideToTray();
                }
            });
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.TrayToolTipText) when _trayIcon is not null:
                // Changes at most once per poll tick (1 s while visible, 5 s while hidden).
                _trayIcon.ToolTipText = _viewModel.TrayToolTipText;
                break;
            case nameof(MainWindowViewModel.TaskbarProgress):
                UpdateTaskbar();
                break;
        }
    }

    private void UpdateTaskbar()
    {
        var progress = _viewModel.TaskbarProgress;
        if (progress == _shownProgress || _window is not { IsVisible: true } || _window.TryGetPlatformHandle() is not { } handle)
        {
            return;
        }

        try
        {
            _taskbar.SetProgress(handle.Handle, progress);
            _shownProgress = progress;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not update the taskbar progress");
        }
    }

    private TrayIcon CreateTrayIcon()
    {
        var menu = new NativeMenu();
        AddItem(menu, Strings.TrayOpen, ShowMainWindow);
        AddItem(menu, Strings.CommandAddUrl, () => _viewModel.AddUrlCommand.Execute(null));
        menu.Items.Add(new NativeMenuItemSeparator());
        AddItem(menu, Strings.CommandPauseAll, () => _viewModel.PauseAllCommand.Execute(null));
        AddItem(menu, Strings.CommandResumeAll, () => _viewModel.ResumeAllCommand.Execute(null));
        menu.Items.Add(new NativeMenuItemSeparator());
        AddItem(menu, Strings.TrayExit, () => _ = ExitAsync());

        // On Windows a left click raises Clicked and a right click opens the menu; on macOS a click opens the menu.
        var trayIcon = new TrayIcon { Icon = LoadIcon(), ToolTipText = _viewModel.TrayToolTipText, Menu = menu, IsVisible = true };
        trayIcon.Clicked += (_, _) => ToggleMainWindow();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { trayIcon });
        return trayIcon;

        static void AddItem(NativeMenu menu, string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private static WindowIcon LoadIcon() => new(AssetLoader.Open(IconUri));
}

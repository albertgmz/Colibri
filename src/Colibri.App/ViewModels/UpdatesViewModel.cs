using Avalonia.Threading;
using Colibri.App.Resources;
using Colibri.Core.Platform;
using Colibri.Core.Settings;
using Colibri.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Colibri.App.ViewModels;

/// <summary>One check/download operation shared by Settings and the desktop lifetime.</summary>
public partial class UpdatesViewModel : ObservableObject
{
    private readonly IReleaseUpdateService _service;
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly IShellService _shell;
    private readonly ILogger<UpdatesViewModel> _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1);
    private CancellationTokenSource? _downloadCancellation;
    private Task? _loop;
    private ReleaseUpdate? _available;
    private VerifiedUpdatePackage? _downloaded;
    private bool _loading;

    public UpdatesViewModel(IReleaseUpdateService service, AppSettings settings, ISettingsStore store,
        IShellService shell, ILogger<UpdatesViewModel> logger, TimeProvider time)
    {
        _service = service; _settings = settings; _store = store; _shell = shell; _logger = logger; _time = time;
        _loading = true; AutoCheckUpdates = settings.AutoCheckUpdates; _loading = false;
    }

    public string CurrentVersion => _service.CurrentVersion;
    public bool SupportsPackages => _service.PackageKind != UpdatePackageKind.None;
    public bool IsInstalled => _service.PackageKind == UpdatePackageKind.Installer;
    public string PackageInstructions => IsInstalled ? Strings.UpdateInstallerInstructions : Strings.UpdatePortableInstructions;
    public event Action? InstallRequested;

    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private string _status = Strings.UpdateNotChecked;
    [ObservableProperty] private string _availableVersion = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload), nameof(CanReveal), nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(DownloadCommand), nameof(RevealCommand), nameof(InstallCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool _hasUpdate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReveal), nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand), nameof(InstallCommand))]
    private bool _hasPackage;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    private bool _isDownloading;

    public bool CanCheck => !IsBusy;
    public bool CanDownload => !IsBusy && HasUpdate && SupportsPackages;
    public bool CanReveal => !IsBusy && HasPackage;
    public bool CanInstall => CanReveal && IsInstalled;

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        if (_loading) return;
        _settings.AutoCheckUpdates = value;
        _ = SavePreferenceAsync();
    }

    private async Task SavePreferenceAsync()
    {
        try { await _store.SaveAsync(_settings, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not save automatic update preference"); Status = Strings.UpdatePreferenceFailed; }
    }

    public void Start() => _loop ??= RunLoopAsync();

    private async Task RunLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                if (_settings.AutoCheckUpdates) await CheckAsync(_lifetime.Token);
                await Task.Delay(TimeSpan.FromHours(1), _time, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task StopAsync()
    {
        await _lifetime.CancelAsync();
        _downloadCancellation?.Cancel();
        if (_loop is not null) await _loop;
        // The desktop exit is posted after InstallCommand completes, so this does not await itself.
        foreach (var task in new[] { CheckNowCommand.ExecutionTask, DownloadCommand.ExecutionTask, RevealCommand.ExecutionTask, InstallCommand.ExecutionTask })
            if (task is not null) await task;
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => CheckAsync(_lifetime.Token);

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!await _operation.WaitAsync(0, cancellationToken)) return;
        try
        {
            await UiAsync(() => { IsBusy = true; Status = Strings.UpdateChecking; });
            var available = await _service.CheckAsync(cancellationToken);
            await UiAsync(() =>
            {
                if (available?.Version != _available?.Version) { _downloaded = null; HasPackage = false; }
                _available = available;
                HasUpdate = available is not null;
                AvailableVersion = available?.Version ?? "";
                Status = _downloaded is not null ? Strings.UpdateReady : available is null ? Strings.UpdateCurrent : Strings.UpdateAvailable;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogDebug("Update check failed ({ErrorType})", ex.GetType().Name);
            await UiAsync(() => Status = ex is UpdateFeedUnavailableException ? Strings.UpdateFeedUnavailable : Strings.UpdateCheckFailed);
        }
        finally { await UiAsync(() => IsBusy = false); _operation.Release(); }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (_available is null || !await _operation.WaitAsync(0, _lifetime.Token)) return;
        try
        {
            IsBusy = true; IsDownloading = true; Status = Strings.UpdateDownloading;
            _downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _downloaded = await _service.DownloadAsync(_available, _downloadCancellation.Token);
            HasPackage = true; Status = Strings.UpdateReady;
        }
        catch (OperationCanceledException) { Status = Strings.UpdateCancelled; }
        catch (Exception ex) { _logger.LogDebug("Update download failed ({ErrorType})", ex.GetType().Name); Status = Strings.UpdateDownloadFailed; }
        finally { _downloadCancellation?.Dispose(); _downloadCancellation = null; IsDownloading = false; IsBusy = false; _operation.Release(); }
    }

    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void CancelDownload() => _downloadCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanReveal))]
    private async Task RevealAsync()
    {
        try { if (_downloaded is not null) await _shell.RevealInFolderAsync(_downloaded.Path); }
        catch (Exception ex) { _logger.LogDebug("Could not reveal update ({ErrorType})", ex.GetType().Name); Status = Strings.UpdateRevealFailed; }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_downloaded is null || InstallRequested is null || !await _operation.WaitAsync(0, _lifetime.Token)) return;
        IsBusy = true;
        try
        {
            await _service.PrepareInstallAfterExitAsync(_downloaded, _lifetime.Token);
            InstallRequested.Invoke();
        }
        catch (Exception ex) { _logger.LogDebug("Update handoff failed ({ErrorType})", ex.GetType().Name); Status = Strings.UpdateInstallFailed; }
        finally { IsBusy = false; _operation.Release(); }
    }

    private static async Task UiAsync(Action action) => await Dispatcher.UIThread.InvokeAsync(action);
}

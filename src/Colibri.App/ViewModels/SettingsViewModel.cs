using System.Collections.ObjectModel;
using Colibri.App.Resources;
using Colibri.App.Services;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Colibri.Core.Services;
using Colibri.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Colibri.App.ViewModels;

/// <summary>
/// The settings page. Every change is applied and saved at once (text boxes when they lose focus); there
/// is no Save button. Values that do not pass validation are not saved.
/// </summary>
/// <remarks>
/// The <see cref="AppSettings"/> object is shared with the download manager, which reads it on background
/// threads; collections in it are replaced, never changed in place.
/// </remarks>
public partial class SettingsViewModel : ObservableObject
{
    public const int MaxConcurrentLimit = 20;
    public const int MaxConnectionsPerServer = 16;
    public const int MaxSpeedLimitKiB = 10_000_000;

    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly DownloadManager _manager;
    private readonly IAutostartService _autostart;
    private readonly IShellService _shell;
    private readonly IAppPaths _paths;
    private readonly IBrowserHostRegistrar? _registrar;
    private readonly NativeHostRegistration _hostRegistration;
    private readonly ILogger<SettingsViewModel> _logger;

    // True while the page copies values in, so those assignments are not taken as user changes.
    private bool _loading;

    // aria2 restarts run one after the other, never two at once.
    private Task _restart = Task.CompletedTask;

    [ObservableProperty]
    private bool _startWithSystem;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _isTrayAvailable = true;

    /// <summary>0 = system, 1 = light, 2 = dark (the order of <see cref="AppTheme"/>).</summary>
    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private string _defaultFolder = string.Empty;

    [ObservableProperty]
    private string? _folderError;

    [ObservableProperty]
    private decimal? _maxConcurrentDownloads;

    [ObservableProperty]
    private decimal? _connectionsPerServer;

    [ObservableProperty]
    private decimal? _speedLimitKiB;

    [ObservableProperty]
    private string _captureExtensions = string.Empty;

    [ObservableProperty]
    private decimal? _captureMinSizeKiB;

    [ObservableProperty]
    private string _aria2Path = string.Empty;

    [ObservableProperty]
    private string? _browserError;

    /// <summary>True when the browser list was read and is empty (on Linux and macOS: no supported browser was run yet).</summary>
    [ObservableProperty]
    private bool _noBrowserFound;

    public SettingsViewModel(
        AppSettings settings,
        ISettingsStore store,
        DownloadManager manager,
        IAutostartService autostart,
        IShellService shell,
        IAppPaths paths,
        ILogger<SettingsViewModel> logger,
        IBrowserHostRegistrar? registrar = null,
        NativeHostRegistration? hostRegistration = null)
    {
        _settings = settings;
        _store = store;
        _manager = manager;
        _autostart = autostart;
        _shell = shell;
        _paths = paths;
        _logger = logger;
        _registrar = registrar;
        _hostRegistration = hostRegistration ?? NativeHostSetup.CreateRegistration();

        CategoryFolders =
        [
            new(this, DownloadCategory.Compressed, Strings.CategoryCompressed),
            new(this, DownloadCategory.Documents, Strings.CategoryDocuments),
            new(this, DownloadCategory.Music, Strings.CategoryMusic),
            new(this, DownloadCategory.Programs, Strings.CategoryPrograms),
            new(this, DownloadCategory.Video, Strings.CategoryVideo),
            new(this, DownloadCategory.Other, Strings.CategoryOther),
        ];
    }

    public bool IsAutostartSupported => _autostart.IsSupported;

    public bool IsBrowserStatusVisible => _registrar is not null;

    /// <summary>One row per browser: its name and whether Colibri's native-messaging host is set up for it.</summary>
    public ObservableCollection<BrowserStatusViewModel> BrowserStatuses { get; } = [];

    /// <summary>Shown as the default folder's placeholder: where downloads go when the box is empty.</summary>
    public string DefaultFolderPlaceholder => _paths.DefaultDownloadsDirectory;

    public ObservableCollection<CategoryFolderViewModel> CategoryFolders { get; }

    public IReadOnlyList<string> ThemeNames { get; } = [Strings.SettingsThemeSystem, Strings.SettingsThemeLight, Strings.SettingsThemeDark];

    /// <summary>Copies the current settings into the page; called each time the page opens.</summary>
    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            StartWithSystem = _settings.StartWithSystem;
            CloseToTray = _settings.CloseToTray;
            MinimizeToTray = _settings.MinimizeToTray;
            ThemeIndex = (int)_settings.Theme;
            DefaultFolder = _settings.DefaultDownloadFolder;
            FolderError = null;
            foreach (var row in CategoryFolders)
            {
                row.Folder = _settings.CategoryFolders.GetValueOrDefault(row.Category) ?? string.Empty;
            }

            UpdateCategoryPlaceholders();
            MaxConcurrentDownloads = _settings.MaxConcurrentDownloads;
            ConnectionsPerServer = _settings.ConnectionsPerServer;
            SpeedLimitKiB = _settings.GlobalSpeedLimitKiB;
            CaptureExtensions = string.Join(", ", _settings.BrowserCaptureExtensions);
            CaptureMinSizeKiB = _settings.BrowserCaptureMinSizeKiB;
            Aria2Path = _settings.Aria2Path;
        }
        finally
        {
            _loading = false;
        }

        // The real autostart state lives in the OS (the user may have removed the entry by hand).
        if (_autostart.IsSupported)
        {
            try
            {
                var enabled = await _autostart.IsEnabledAsync();
                SetQuietly(() => StartWithSystem = enabled);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the autostart state");
            }
        }

        await RefreshBrowserStatusAsync();
    }

    /// <summary>The task of the last save, so tests can wait for it.</summary>
    internal Task PendingWork { get; private set; } = Task.CompletedTask;

    partial void OnStartWithSystemChanged(bool value)
    {
        if (!_loading)
        {
            Run(ApplyAutostartAsync(value));
        }
    }

    partial void OnCloseToTrayChanged(bool value) => Change(() => _settings.CloseToTray = value);

    partial void OnMinimizeToTrayChanged(bool value) => Change(() => _settings.MinimizeToTray = value);

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading || !Enum.IsDefined((AppTheme)value))
        {
            return;
        }

        ThemeService.Apply((AppTheme)value);
        Change(() => _settings.Theme = (AppTheme)value);
    }

    partial void OnDefaultFolderChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        if (!IsFolderValid(value))
        {
            FolderError = Strings.AddUrlFolderNotFullPath;
            return;
        }

        FolderError = null;
        Change(() => _settings.DefaultDownloadFolder = value.Trim());
        UpdateCategoryPlaceholders();
    }

    partial void OnMaxConcurrentDownloadsChanged(decimal? value) =>
        ChangeEngineNumber(value, 1, MaxConcurrentLimit, v => _settings.MaxConcurrentDownloads = v);

    partial void OnConnectionsPerServerChanged(decimal? value) =>
        ChangeEngineNumber(value, 1, MaxConnectionsPerServer, v => _settings.ConnectionsPerServer = v);

    partial void OnSpeedLimitKiBChanged(decimal? value) =>
        ChangeEngineNumber(value, 0, MaxSpeedLimitKiB, v => _settings.GlobalSpeedLimitKiB = v);

    partial void OnCaptureExtensionsChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        var extensions = ParseExtensions(value);
        Change(() => _settings.BrowserCaptureExtensions = extensions);

        // Show the cleaned-up list.
        SetQuietly(() => CaptureExtensions = string.Join(", ", extensions));
    }

    partial void OnCaptureMinSizeKiBChanged(decimal? value)
    {
        if (!_loading && value is { } number)
        {
            Change(() => _settings.BrowserCaptureMinSizeKiB = (int)Math.Clamp(number, 0, int.MaxValue));
        }
    }

    partial void OnAria2PathChanged(string value)
    {
        if (_loading || value.Trim() == _settings.Aria2Path)
        {
            return;
        }

        Change(() => _settings.Aria2Path = value.Trim());

        // aria2 reads its path only at start; restart it off the UI thread, after any restart still running.
        var previous = _restart;
        _restart = Task.Run(async () =>
        {
            await previous;
            await _manager.RestartEnginesAsync(CancellationToken.None);
        });
        Run(_restart);
    }

    /// <summary>Called by a category row when its folder changes.</summary>
    internal void OnCategoryFolderChanged(CategoryFolderViewModel row)
    {
        if (_loading)
        {
            return;
        }

        if (!IsFolderValid(row.Folder))
        {
            FolderError = Strings.AddUrlFolderNotFullPath;
            return;
        }

        FolderError = null;
        var folders = new Dictionary<DownloadCategory, string>(_settings.CategoryFolders);
        if (string.IsNullOrWhiteSpace(row.Folder))
        {
            folders.Remove(row.Category);
        }
        else
        {
            folders[row.Category] = row.Folder.Trim();
        }

        Change(() => _settings.CategoryFolders = folders);
    }

    [RelayCommand]
    private Task OpenLogsFolderAsync() => RunSafeAsync(() =>
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        return _shell.OpenFolderAsync(_paths.LogsDirectory);
    });

    /// <summary>
    /// Registers the native-messaging host next to this app with the browsers (also repairs a registration
    /// that points at another copy of Colibri), then shows the new state.
    /// </summary>
    [RelayCommand]
    private async Task InstallBrowserIntegrationAsync()
    {
        if (_registrar is null)
        {
            return;
        }

        BrowserError = null;
        try
        {
            await _registrar.RegisterAsync(_hostRegistration);
            _logger.LogInformation("Registered the browser native-messaging host {Path}", _hostRegistration.HostExecutablePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not register the browser native-messaging host");
            BrowserError = Strings.BrowserInstallFailed;
        }

        await RefreshBrowserStatusAsync();
    }

    private async Task RefreshBrowserStatusAsync()
    {
        if (_registrar is null)
        {
            return;
        }

        try
        {
            var statuses = await _registrar.GetStatusAsync(_hostRegistration);
            BrowserStatuses.Clear();
            foreach (var status in statuses)
            {
                BrowserStatuses.Add(new BrowserStatusViewModel(BrowserName(status.Browser), StatusText(status.Status)));
            }

            NoBrowserFound = statuses.Count == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the browser integration state");
        }

        static string BrowserName(BrowserKind browser) => browser switch
        {
            BrowserKind.Chrome => Strings.BrowserChrome,
            BrowserKind.Edge => Strings.BrowserEdge,
            _ => Strings.BrowserChromium,
        };

        static string StatusText(BrowserIntegrationStatus status) => status switch
        {
            BrowserIntegrationStatus.Registered => Strings.BrowserStatusRegistered,
            BrowserIntegrationStatus.Outdated => Strings.BrowserStatusOutdated,
            _ => Strings.BrowserStatusNotRegistered,
        };
    }

    private async Task ApplyAutostartAsync(bool enabled)
    {
        try
        {
            await _autostart.SetEnabledAsync(enabled);
            _settings.StartWithSystem = enabled;
            await SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not change autostart");
            SetQuietly(() => StartWithSystem = !enabled);
        }
    }

    private void ChangeEngineNumber(decimal? value, int min, int max, Action<int> set)
    {
        // An empty box (null) keeps the old value; the control puts out-of-range input back in range.
        if (_loading || value is not { } number)
        {
            return;
        }

        set((int)Math.Clamp(Math.Round(number), min, max));
        Run(SaveAndApplyEngineOptionsAsync());
    }

    private async Task SaveAndApplyEngineOptionsAsync()
    {
        await SaveAsync();
        await _manager.ApplyEngineOptionsAsync(
            new EngineOptions(_settings.MaxConcurrentDownloads, _settings.ConnectionsPerServer, _settings.GlobalSpeedLimitKiB * 1024L),
            CancellationToken.None);
    }

    private void Change(Action apply)
    {
        if (_loading)
        {
            return;
        }

        apply();
        Run(SaveAsync());
    }

    private async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(_settings, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the settings");
        }
    }

    /// <summary>Keeps track of background work so tests can wait for it; failures are already logged.</summary>
    private void Run(Task work) => PendingWork = Task.WhenAll(PendingWork, work);

    private async Task RunSafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A settings action failed");
        }
    }

    private void SetQuietly(Action set)
    {
        _loading = true;
        try
        {
            set();
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateCategoryPlaceholders()
    {
        foreach (var row in CategoryFolders)
        {
            row.Placeholder = Path.Combine(
                string.IsNullOrWhiteSpace(_settings.DefaultDownloadFolder) ? _paths.DefaultDownloadsDirectory : _settings.DefaultDownloadFolder,
                row.Category.ToString());
        }
    }

    // A relative folder would be resolved against a different working directory by aria2.
    private static bool IsFolderValid(string? folder) => string.IsNullOrWhiteSpace(folder) || Path.IsPathFullyQualified(folder.Trim());

    /// <summary>"ZIP, .rar  7z" -> ["zip", "rar", "7z"]: lower case, no dots, letters and digits only, no repeats.</summary>
    internal static List<string> ParseExtensions(string text) =>
        text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length is > 0 and <= 16 && e.All(char.IsAsciiLetterOrDigit))
            .Distinct()
            .ToList();
}

/// <summary>One browser in the browser integration list.</summary>
public sealed record BrowserStatusViewModel(string Name, string Status);

/// <summary>One row of the per-category folders.</summary>
public partial class CategoryFolderViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private string _placeholder = string.Empty;

    public CategoryFolderViewModel(SettingsViewModel owner, DownloadCategory category, string label)
    {
        _owner = owner;
        Category = category;
        Label = label;
    }

    public DownloadCategory Category { get; }

    public string Label { get; }

    partial void OnFolderChanged(string value) => _owner.OnCategoryFolderChanged(this);

    /// <summary>Clears the folder, so the category uses a subfolder of the default folder again.</summary>
    [RelayCommand]
    private void UseDefault() => Folder = string.Empty;
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Collections;
using Avalonia.Threading;
using Colibri.App.Formatting;
using Colibri.App.Resources;
using Colibri.App.Services;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Colibri.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Colibri.App.ViewModels;

/// <summary>
/// The main window: navigation, command bar, download table and status bar.
/// </summary>
/// <remarks>
/// All rows live in one collection, shown through a <see cref="DataGridCollectionView"/> that filters
/// (navigation entry + search text) and sorts. The filter is re-applied only when something it depends
/// on changes, never on a plain progress tick.
/// </remarks>
public partial class MainWindowViewModel : ObservableObject
{
    public static readonly TimeSpan VisiblePollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan HiddenPollInterval = TimeSpan.FromSeconds(5);

    private readonly DownloadManager _manager;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly ObservableCollection<DownloadItemViewModel> _items = [];
    private readonly Dictionary<Guid, DownloadItemViewModel> _byId = [];
    private IReadOnlyList<DownloadItemViewModel> _selectedItems = [];

    [ObservableProperty]
    private NavItemViewModel _selectedNav;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _activeText = string.Empty;

    [ObservableProperty]
    private string _engineStateText = string.Empty;

    [ObservableProperty]
    private bool _isEngineMissing;

    public MainWindowViewModel(DownloadManager manager, IShellService shell, IDialogService dialogs, ILogger<MainWindowViewModel> logger)
    {
        _manager = manager;
        _shell = shell;
        _dialogs = dialogs;
        _logger = logger;

        NavItems =
        [
            new NavItemViewModel(Strings.NavAll, "IconNavAll", NavFilter.All),
            new NavItemViewModel(Strings.NavActive, "IconNavActive", NavFilter.Active),
            new NavItemViewModel(Strings.NavCompleted, "IconNavCompleted", NavFilter.Completed),
            new NavItemViewModel(Strings.NavFailed, "IconNavFailed", NavFilter.Failed),
            new NavItemViewModel(Strings.NavCategories, string.Empty, NavFilter.Header),
            Category(Strings.CategoryCompressed, DownloadCategory.Compressed),
            Category(Strings.CategoryDocuments, DownloadCategory.Documents),
            Category(Strings.CategoryMusic, DownloadCategory.Music),
            Category(Strings.CategoryPrograms, DownloadCategory.Programs),
            Category(Strings.CategoryVideo, DownloadCategory.Video),
            Category(Strings.CategoryOther, DownloadCategory.Other),
        ];
        _selectedNav = NavItems[0];

        Downloads = new DataGridCollectionView(_items) { Filter = item => IsVisible((DownloadItemViewModel)item) };
        Downloads.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(DownloadItemViewModel.AddedAt), ListSortDirection.Descending));

        SpeedText = Format(Strings.StatusSpeedFormat, DisplayFormat.Speed(0));
        ActiveText = Format(Strings.StatusActiveFormat, 0);
        ShowEngineState(manager.EngineState);

        // Raised on background threads: hop to the UI thread. One post per batch keeps a tick cheap.
        _manager.ItemAdded += (_, item) => Dispatcher.UIThread.Post(() => AddOrUpdate([item], added: true));
        _manager.ItemsUpdated += (_, items) => Dispatcher.UIThread.Post(() => AddOrUpdate(items, added: false));
        _manager.ItemRemoved += (_, id) => Dispatcher.UIThread.Post(() => Remove(id));
        _manager.GlobalStatsChanged += (_, stats) => Dispatcher.UIThread.Post(() => ShowStats(stats));
        _manager.EngineStateChanged += (_, state) => Dispatcher.UIThread.Post(() => ShowEngineState(state));

        static NavItemViewModel Category(string label, DownloadCategory category) =>
            new(label, "IconCategory" + category, NavFilter.Category, category);
    }

    public IReadOnlyList<NavItemViewModel> NavItems { get; }

    /// <summary>The rows the table shows (filtered and sorted).</summary>
    public DataGridCollectionView Downloads { get; }

    /// <summary>Every row, whatever the filter.</summary>
    public IReadOnlyList<DownloadItemViewModel> AllItems => _items;

    /// <summary>The selected rows; set by the view (the DataGrid's selection cannot be bound).</summary>
    public IReadOnlyList<DownloadItemViewModel> SelectedItems
    {
        get => _selectedItems;
        set
        {
            _selectedItems = value;
            NotifyCommands();
        }
    }

    /// <summary>Starts the download manager (engine, reconcile, polling) and loads the rows.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            // Off the UI thread: starting aria2 and reading the database take a moment.
            await Task.Run(() => _manager.InitializeAsync(CancellationToken.None));
            AddOrUpdate(await _manager.GetItemsAsync(CancellationToken.None), added: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Starting the download manager failed");
        }
    }

    /// <summary>Polls quickly while the window is visible and slowly while it is hidden or minimized.</summary>
    public void SetWindowVisible(bool visible) =>
        _manager.SetPollingInterval(visible ? VisiblePollInterval : HiddenPollInterval);

    /// <summary>Stops polling and the engine; called when the app exits.</summary>
    public Task ShutdownAsync() => _manager.StopAsync();

    public AddUrlViewModel CreateAddUrl(LinkContext context, string? url) => new(_manager, context, url, _logger);

    /// <summary>Double-click on a row: opens a completed file.</summary>
    public Task OpenItemAsync(DownloadItemViewModel item) =>
        item.State == DownloadState.Completed ? RunSafeAsync(() => _shell.OpenFileAsync(item.FilePath), "open the file") : Task.CompletedTask;

    partial void OnSelectedNavChanged(NavItemViewModel value) => Downloads.Refresh();

    partial void OnSearchTextChanged(string value) => Downloads.Refresh();

    [RelayCommand]
    private async Task AddUrlAsync()
    {
        string? url = null;
        try
        {
            var text = (await _dialogs.ReadClipboardTextAsync())?.Trim();
            if (UrlPolicy.TryValidate(text, out _, out string? _))
            {
                url = text;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the clipboard");
        }

        _dialogs.ShowAddUrl(CreateAddUrl(LinkContext.Empty, url));
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => RunSafeAsync(() => _manager.ResumeAsync(SelectedIds(), CancellationToken.None), "resume");

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task PauseAsync() => RunSafeAsync(() => _manager.PauseAsync(SelectedIds(), CancellationToken.None), "pause");

    [RelayCommand(CanExecute = nameof(CanPauseAll))]
    private Task PauseAllAsync() => RunSafeAsync(() => _manager.PauseAllAsync(CancellationToken.None), "pause all");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => RunSafeAsync(() => _manager.DeleteAsync(SelectedIds(), deleteFiles: false, CancellationToken.None), "delete");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteWithFileAsync() => RunSafeAsync(() => _manager.DeleteAsync(SelectedIds(), deleteFiles: true, CancellationToken.None), "delete");

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync() => OpenItemAsync(_selectedItems[0]);

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task ShowInFolderAsync() => RunSafeAsync(() => _shell.RevealInFolderAsync(_selectedItems[0].FilePath), "show the folder");

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task CopyUrlAsync() => RunSafeAsync(() => _dialogs.WriteClipboardTextAsync(_selectedItems[0].Url), "copy the URL");

    [RelayCommand]
    private void Settings() => _dialogs.ShowSettings();

    [RelayCommand]
    private Task RetryEngineAsync() => RunSafeAsync(() => Task.Run(() => _manager.RetryEngineAsync(CancellationToken.None)), "start aria2");

    private bool CanResume() => _selectedItems.Any(i => i.State is DownloadState.Paused or DownloadState.Failed);

    private bool CanPause() => _selectedItems.Any(i => i.State is DownloadState.Active or DownloadState.Queued);

    private bool CanPauseAll() => _items.Any(i => i.State is DownloadState.Active or DownloadState.Queued);

    private bool HasSelection() => _selectedItems.Count > 0;

    private bool HasSingleSelection() => _selectedItems.Count == 1;

    private bool CanOpen() => _selectedItems is [{ State: DownloadState.Completed }];

    private List<Guid> SelectedIds() => _selectedItems.Select(i => i.Id).ToList();

    private bool IsVisible(DownloadItemViewModel item) =>
        SelectedNav.Matches(item)
        && (string.IsNullOrWhiteSpace(SearchText) || item.FileName.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private void AddOrUpdate(IEnumerable<DownloadItem> snapshots, bool added)
    {
        var refilter = false;
        var countsChanged = false;
        foreach (var snapshot in snapshots)
        {
            if (_byId.TryGetValue(snapshot.Id, out var row))
            {
                var wasVisible = IsVisible(row);
                var oldState = row.State;
                var oldCategory = row.Category;
                row.Update(snapshot);
                countsChanged |= row.State != oldState || row.Category != oldCategory;
                refilter |= IsVisible(row) != wasVisible;
            }
            else if (added)
            {
                // Updates for rows not loaded yet are skipped: the initial load brings them in.
                row = new DownloadItemViewModel(snapshot);
                _byId[row.Id] = row;
                _items.Add(row); // The collection view filters and sorts the new row by itself.
                countsChanged = true;
            }
        }

        if (refilter)
        {
            // Only when a row moved in or out of the current filter (e.g. completed while "Active" is shown).
            Downloads.Refresh();
        }

        if (countsChanged)
        {
            UpdateCounts();
            NotifyCommands();
        }
    }

    private void Remove(Guid id)
    {
        if (_byId.Remove(id, out var row))
        {
            _items.Remove(row);
            UpdateCounts();
            NotifyCommands();
        }
    }

    private void UpdateCounts()
    {
        foreach (var nav in NavItems.Where(n => !n.IsHeader))
        {
            nav.Count = _items.Count(nav.Matches);
        }
    }

    private void NotifyCommands()
    {
        ResumeCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        PauseAllCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DeleteWithFileCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        ShowInFolderCommand.NotifyCanExecuteChanged();
        CopyUrlCommand.NotifyCanExecuteChanged();
    }

    private void ShowStats(EngineGlobalStats stats)
    {
        // aria2's total speed is an average that fades out for a few seconds after the last download
        // stops; with nothing active the speed is simply zero.
        var speed = stats.NumActive > 0 ? stats.DownloadSpeed : 0;
        SpeedText = Format(Strings.StatusSpeedFormat, DisplayFormat.Speed(speed));
        ActiveText = Format(Strings.StatusActiveFormat, stats.NumActive);
    }

    private void ShowEngineState(EngineState state)
    {
        EngineStateText = state switch
        {
            EngineState.Running => Strings.EngineStateRunning,
            EngineState.Starting => Strings.EngineStateStarting,
            EngineState.Restarting => Strings.EngineStateRestarting,
            EngineState.Failed => Strings.EngineStateFailed,
            EngineState.NotFound => Strings.EngineStateNotFound,
            _ => Strings.EngineStateStopped,
        };
        IsEngineMissing = state == EngineState.NotFound;
    }

    private async Task RunSafeAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // Commands must never crash the app; the download manager already logs the details.
            _logger.LogError(ex, "Could not {Action}", what);
        }
    }

    private static string Format(string format, object value) => string.Format(CultureInfo.CurrentCulture, format, value);
}

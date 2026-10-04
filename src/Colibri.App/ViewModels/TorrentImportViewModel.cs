using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Colibri.App.Formatting;
using Colibri.App.Resources;
using Colibri.Core.Models;
using Colibri.Core.Engine;
using Colibri.Core.Queues;
using Colibri.Core.Services;
using Colibri.Core.Torrents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Colibri.App.ViewModels;

public partial class TorrentFileChoice : ObservableObject
{
    public TorrentFile File { get; }
    public string Name => File.RelativePath;
    public string Size => DisplayFormat.Size(File.Length);
    [ObservableProperty] private bool _isSelected;
    public TorrentFileChoice(TorrentFile file, bool selected) { File = file; IsSelected = selected; }
}

public partial class TorrentImportViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly CancellationTokenSource _lifetime = new();
    private TorrentMetadataResult? _preview;
    private Guid? _editingId;
    private bool _closed;
    public ObservableCollection<TorrentFileChoice> Files { get; } = [];
    public ObservableCollection<DownloadQueue> Queues { get; } = [];
    public event EventHandler? CloseRequested;
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _infoHash = "";
    [ObservableProperty] private string _saveFolder = "";
    [ObservableProperty] private DownloadQueue? _selectedQueue;
    [ObservableProperty] private bool _isMagnet;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasMetadata;
    [ObservableProperty] private bool _isPrivate;
    [ObservableProperty] private bool _seedAfterCompletion;
    [ObservableProperty] private decimal _seedRatio = 1;
    [ObservableProperty] private int _seedMinutes = 60;
    [ObservableProperty] private int _uploadLimitKiB = 64;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string _uploaded = DisplayFormat.Size(0);
    [ObservableProperty] private string _uploadSpeed = DisplayFormat.Speed(0);
    [ObservableProperty] private bool _isSeeding;
    public string SelectedSize => DisplayFormat.Size(Files.Where(file => file.IsSelected).Sum(file => file.File.Length));
    public bool IsNewDownload => _editingId is null;
    public string ConfirmLabel => _editingId is null ? Strings.TorrentConfirm : Strings.TorrentApply;
    public bool CanFetchMetadata => IsMagnet && !HasMetadata && !IsBusy && !_closed;
    public bool CanImport => HasMetadata && Files.Any(file => file.IsSelected) && !IsBusy && !_closed;

    public TorrentImportViewModel(DownloadManager manager)
    {
        _manager = manager;
        SaveFolder = manager.GetCategoryFolder(DownloadCategory.Other);
        _manager.ItemsUpdated += OnItemsUpdated;
    }

    public async Task LoadSourceAsync(string source) => await ExecuteAsync(async () =>
    {
        Source = source;
        await LoadQueuesAsync();
        if (_closed) return;
        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            InfoHash = MagnetLink.Parse(source).InfoHash;
            IsMagnet = true;
            Status = Strings.TorrentMetadataHelp;
            UpdateCommands();
        }
        else
        {
            var preview = await _manager.LoadTorrentFileAsync(source, _lifetime.Token);
            if (!_closed) SetPreview(preview);
        }
    });

    public async Task LoadDownloadAsync(DownloadItem item) => await ExecuteAsync(async () =>
    {
        if (item.Torrent is not { } torrent) throw new ArgumentException(Strings.TorrentInvalid);
        _editingId = item.Id;
        Source = item.Url;
        SaveFolder = item.SaveFolder;
        await LoadQueuesAsync();
        if (_closed) return;
        SelectedQueue = Queues.FirstOrDefault(queue => queue.Id == item.QueueId);
        SetPreview(TorrentMetainfo.Parse(torrent.Metainfo), torrent.SelectedFileIndices);
        SeedAfterCompletion = torrent.SeedOptions.Enabled;
        SeedRatio = (decimal)torrent.SeedOptions.Ratio;
        SeedMinutes = torrent.SeedOptions.TimeLimitMinutes;
        UploadLimitKiB = (int)(torrent.SeedOptions.UploadLimitBytesPerSecond / 1024);
        UpdateStats(item);
        OnPropertyChanged(nameof(ConfirmLabel));
        OnPropertyChanged(nameof(IsNewDownload));
    });

    private async Task LoadQueuesAsync()
    {
        Queues.Clear();
        foreach (var queue in await _manager.GetQueuesAsync(_lifetime.Token)) Queues.Add(queue);
        SelectedQueue = Queues.FirstOrDefault(queue => queue.Id == Guid.Empty);
    }

    private void SetPreview(TorrentMetadataResult preview, IReadOnlyList<int>? selected = null)
    {
        _preview = preview;
        Name = preview.Metadata.Name;
        InfoHash = preview.Metadata.InfoHash;
        IsPrivate = preview.Metadata.IsPrivate;
        foreach (var file in Files) file.PropertyChanged -= OnFileChanged;
        Files.Clear();
        foreach (var file in preview.Metadata.Files)
        {
            var choice = new TorrentFileChoice(file, selected?.Contains(file.Index) ?? true);
            choice.PropertyChanged += OnFileChanged;
            Files.Add(choice);
        }
        HasMetadata = true;
        Status = Strings.TorrentMetadataReady;
        OnPropertyChanged(nameof(SelectedSize));
        UpdateCommands();
    }
    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    { OnPropertyChanged(nameof(SelectedSize)); UpdateCommands(); }
    partial void OnIsBusyChanged(bool value) => UpdateCommands();
    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanFetchMetadata));
        OnPropertyChanged(nameof(CanImport));
        FetchMetadataCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanFetchMetadata))]
    private async Task FetchMetadataAsync() => await ExecuteAsync(async () =>
    {
        var preview = await _manager.FetchMagnetMetadataAsync(Source, _lifetime.Token);
        if (!_closed) SetPreview(preview);
    });

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync() => await ExecuteAsync(async () =>
    {
        if (_preview is null || SelectedQueue is null) throw new ArgumentException(Strings.TorrentChooseFiles);
        var selected = Files.Where(file => file.IsSelected).Select(file => file.File.Index).ToArray();
        var seed = new TorrentSeedOptions(SeedAfterCompletion, (double)SeedRatio, SeedMinutes, UploadLimitKiB * 1024L);
        if (_editingId is { } id)
        {
            await _manager.UpdateTorrentAsync(id, selected, seed, _lifetime.Token);
            await _manager.MoveToQueueAsync([id], SelectedQueue.Id, _lifetime.Token);
            Status = Strings.TorrentPausedSaved;
        }
        else
        {
            var item = await _manager.AddTorrentAsync(_preview, selected, SaveFolder, SelectedQueue.Id, seed, _lifetime.Token);
            if (item.State == DownloadState.Failed) { Error = Strings.TorrentInvalid; return; }
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    });

    [RelayCommand] private void SelectAll() { foreach (var file in Files) file.IsSelected = true; }
    [RelayCommand] private void SelectNone() { foreach (var file in Files) file.IsSelected = false; }
    [RelayCommand] private async Task StopSeedingAsync() => await ExecuteAsync(async () =>
    {
        if (_editingId is { } id) await _manager.StopSeedingAsync(id, _lifetime.Token);
    });
    [RelayCommand] private void Cancel() { WindowClosed(); CloseRequested?.Invoke(this, EventArgs.Empty); }
    public void WindowClosed()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _manager.ItemsUpdated -= OnItemsUpdated;
        foreach (var file in Files) file.PropertyChanged -= OnFileChanged;
    }
    private void OnItemsUpdated(object? sender, IReadOnlyList<DownloadItem> items)
    {
        var item = items.FirstOrDefault(item => item.Id == _editingId);
        if (item is not null) Dispatcher.UIThread.Post(() => { if (!_closed) UpdateStats(item); });
    }
    private void UpdateStats(DownloadItem item)
    { Uploaded = DisplayFormat.Size(item.UploadedBytes); UploadSpeed = DisplayFormat.Speed(item.UploadSpeed); IsSeeding = item.IsSeeding; }
    private async Task ExecuteAsync(Func<Task> action)
    {
        Error = null; IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (EngineOperationException ex) { if (!_closed) Error = ex.Message; }
        catch (FormatException ex) when (ex.Message == "BitTorrent v2 metainfo is unsupported.") { if (!_closed) Error = Strings.TorrentV1Only; }
        catch (Exception) { if (!_closed) Error = Strings.TorrentInvalid; }
        finally { IsBusy = false; }
    }
}

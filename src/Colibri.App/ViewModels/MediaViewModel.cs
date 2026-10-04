using System.Collections.ObjectModel;
using Colibri.App.Resources;
using Colibri.Core.Media;
using Colibri.Core.Models;
using Colibri.Core.Queues;
using Colibri.Core.Services;
using Colibri.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Colibri.App.ViewModels;

public partial class MediaViewModel(DownloadManager manager, IMediaHelper helper, AppSettings settings) : ObservableObject
{
    private CancellationTokenSource? _operation;
    private string? _inspectedSource;
    private LinkContext _context = LinkContext.Empty;
    public event EventHandler? CloseRequested;
    public ObservableCollection<MediaFormat> Formats { get; } = [];
    public ObservableCollection<MediaFormat> AudioFormats { get; } = [];
    public ObservableCollection<DownloadQueue> Queues { get; } = [];
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _fileName = "media";
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _toolStatus = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private MediaFormat? _selectedFormat;
    [ObservableProperty] private MediaFormat? _selectedAudio;
    [ObservableProperty] private DownloadQueue? _selectedQueue;
    public bool NeedsAudio => SelectedFormat is { HasVideo: true, HasAudio: false };
    public string OutputExtension => NeedsAudio && SelectedAudio is not null ? "mkv" : SelectedFormat?.Extension ?? "";
    public string OutputPreview => SelectedFormat is null ? "" : FileNameSanitizer.Sanitize(FileName) + "." + OutputExtension;
    public string QueueStatus => SelectedQueue?.IsRunning == true ? Strings.QueueStatusRunning : Strings.QueueStatusStopped;
    public bool CanInspect => !IsBusy && !string.IsNullOrWhiteSpace(Url);
    public bool CanDownload => !IsBusy && SelectedFormat is not null && SelectedQueue is not null && (!NeedsAudio || SelectedAudio is not null) && _inspectedSource == Url.Trim();
    public async Task LoadAsync(CancellationToken ct)
    {
        Queues.Clear(); foreach (var queue in await manager.GetQueuesAsync(ct)) Queues.Add(queue);
        SelectedQueue = Queues.FirstOrDefault();
        Folder = string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder) ? "" : settings.DefaultDownloadFolder;
    }
    public void SetContext(LinkContext context) => _context = context;
    partial void OnUrlChanged(string value) { _inspectedSource = null; Formats.Clear(); AudioFormats.Clear(); SelectedFormat = null; SelectedAudio = null; RefreshCommands(); }
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnSelectedFormatChanged(MediaFormat? value) { SelectedAudio = null; OnPropertyChanged(nameof(NeedsAudio)); UpdatePreview(); }
    partial void OnSelectedAudioChanged(MediaFormat? value) => UpdatePreview();
    partial void OnFileNameChanged(string value) => UpdatePreview();
    partial void OnSelectedQueueChanged(DownloadQueue? value) { OnPropertyChanged(nameof(QueueStatus)); RefreshCommands(); }
    private void UpdatePreview() { OnPropertyChanged(nameof(OutputExtension)); OnPropertyChanged(nameof(OutputPreview)); RefreshCommands(); }
    private void RefreshCommands() { OnPropertyChanged(nameof(CanInspect)); OnPropertyChanged(nameof(CanDownload)); InspectCommand.NotifyCanExecuteChanged(); DownloadCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanInspect))]
    private async Task InspectAsync()
    {
        Error = null; _operation = new(); IsBusy = true;
        try {
            if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri)) { Error = Strings.MediaInvalidUrl; return; }
            MediaPolicy.Validate(settings.DefaultNetworkPolicy, _context);
            var tools = await helper.ValidateToolsAsync(_operation.Token);
            ToolStatus = $"yt-dlp {tools.YtDlpVersion} · {tools.YtDlpLicense}" + (tools.FfmpegVersion is null ? "" : $"\n{tools.FfmpegVersion} · {tools.FfmpegLicense}");
            var source = Url.Trim();
            var metadata = await helper.InspectAsync(uri, _context, settings.DefaultNetworkPolicy, _operation.Token);
            if (Url.Trim() != source) return;
            Formats.Clear(); AudioFormats.Clear();
            foreach (var format in metadata.Formats.OrderByDescending(f => f.Height ?? 0)) { Formats.Add(format); if (!format.HasVideo && format.HasAudio) AudioFormats.Add(format); }
            Title = metadata.Title; FileName = FileNameSanitizer.Sanitize(metadata.Title); _inspectedSource = source;
            SelectedFormat = Formats.FirstOrDefault(f => f.HasVideo && f.HasAudio) ?? Formats.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_operation.Token.IsCancellationRequested) { Error = Strings.MediaCancelled; }
        catch (MediaHelperException ex) { Error = ex.Message; }
        catch (Exception) { Error = Strings.MediaOperationFailed; }
        finally { IsBusy = false; _operation.Dispose(); _operation = null; }
    }
    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (SelectedFormat is not { } format || SelectedQueue is not { } queue || _inspectedSource != Url.Trim()) return;
        Error = null; _operation = new(); IsBusy = true;
        try {
            var selection = new MediaSelection(format.Id, NeedsAudio ? SelectedAudio?.Id : null, OutputExtension);
            if (string.IsNullOrWhiteSpace(Folder)) Folder = manager.GetCategoryFolder(CategoryMapper.FromFileName(OutputPreview));
            if (!Path.IsPathFullyQualified(Folder)) { Error = Strings.AddUrlFolderNotFullPath; return; }
            var item = await manager.AddMediaAsync(Url.Trim(), _context, selection, FileName, Folder, queue.Id, settings.DefaultNetworkPolicy, _operation.Token);
            if (item.State == DownloadState.Failed) { Error = Strings.MediaOperationFailed; return; }
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (_operation.Token.IsCancellationRequested) { Error = Strings.MediaCancelled; }
        catch (MediaHelperException ex) { Error = ex.Message; }
        catch (Exception) { Error = Strings.MediaOperationFailed; }
        finally { IsBusy = false; _operation.Dispose(); _operation = null; }
    }
    [RelayCommand] private void Cancel() { _operation?.Cancel(); if (!IsBusy) CloseRequested?.Invoke(this, EventArgs.Empty); }
    public void WindowClosed() => CancelPending();
    public void CancelPending() => _operation?.Cancel();
}

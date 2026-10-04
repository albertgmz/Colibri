using System.Collections.ObjectModel;
using Avalonia.Threading;
using Colibri.Core.Ipc;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Colibri.Core.Platform;
using Colibri.Core.Queues;
using Colibri.App.Resources;

namespace Colibri.App.ViewModels;

public partial class BulkLinkViewModel(AddRequest request, DownloadManager? manager = null) : ObservableObject
{
    public AddRequest Request { get; } = request;
    public string Url => Request.FinalUrl ?? Request.Url;
    public string DisplayUrl => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? UrlPolicy.Redact(uri) : "";
    public string FileName => FileNameSanitizer.Sanitize(Request.Context.FileName ??
        (Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "") : ""));
    public DownloadCategory Category => CategoryMapper.FromFileName(FileName);
    public string Destination => manager?.GetCategoryFolder(Category) ?? "";
    [ObservableProperty] private bool _selected = true;
}

/// <summary>Nothing starts until checked links are confirmed.</summary>
public partial class BulkAddViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly CaptureSession _capture;
    private readonly IReadOnlyList<BulkLinkViewModel> _links;
    private readonly IShellService? _shell;
    private bool _reassociating;
    public ObservableCollection<DownloadQueue> Queues { get; } = [];
    [ObservableProperty] private DownloadQueue? _selectedQueue;
    public ObservableCollection<BulkLinkViewModel> VisibleLinks { get; } = [];
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string? _error;
    public event EventHandler? CloseRequested;
    public BulkAddViewModel(DownloadManager manager, IReadOnlyList<AddRequest> links, CaptureSession capture, IShellService? shell = null)
    {
        _manager = manager;
        _capture = capture;
        _shell = shell;
        // A duplicate URL with distinct authentication/context remains a distinct item.
        _links = links.DistinctBy(IpcProtocol.SerializeRequest).Select(link => new BulkLinkViewModel(link, manager)).ToArray();
        OnFilterChanged("");
        capture.Token.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (!_reassociating) CloseRequested?.Invoke(this, EventArgs.Empty);
        }));
    }
    public async Task LoadQueuesAsync()
    {
        Queues.Clear();
        foreach (var queue in await _manager.GetQueuesAsync(CancellationToken.None)) Queues.Add(queue);
        SelectedQueue = Queues.FirstOrDefault(q => q.Id == Guid.Empty);
    }
    partial void OnFilterChanged(string value)
    {
        VisibleLinks.Clear();
        foreach (var link in _links.Where(link => link.DisplayUrl.Contains(value, StringComparison.OrdinalIgnoreCase) ||
            link.FileName.Contains(value, StringComparison.OrdinalIgnoreCase))) VisibleLinks.Add(link);
    }
    [RelayCommand] private void SelectVisible() { foreach (var link in VisibleLinks) link.Selected = true; }
    [RelayCommand] private void ClearVisible() { foreach (var link in VisibleLinks) link.Selected = false; }
    [RelayCommand] private async Task DownloadAsync()
    {
        if (_capture.Token.IsCancellationRequested) return;
        using var operation = _capture.TryBeginOperation();
        if (operation is null) return;
        var added = new List<Guid>();
        try
        {
            foreach (var link in _links.Where(link => link.Selected))
            {
                var items = await _manager.AddAsync(link.Url, link.Request.Context, link.Request.Context.FileName, null, _capture.Token, SelectedQueue?.Id);
                added.AddRange(items.Select(item => item.Id));
                if (items.Count == 0 || items.Any(item => item.State == DownloadState.Failed))
                {
                    if (added.Count > 0) await _manager.StopForBrowserFallbackAsync(added, CancellationToken.None);
                    Error = Resources.Strings.BulkAddFailed;
                    return;
                }
            }
            if (added.Count == 0) return;
            if (!_capture.Accept()) await _manager.StopForBrowserFallbackAsync(added, CancellationToken.None);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (ex is DownloadCleanupException) operation.CleanupFailed();
            else if (added.Count > 0)
            {
                try { await _manager.StopForBrowserFallbackAsync(added, CancellationToken.None); }
                catch (DownloadCleanupException) { operation.CleanupFailed(); }
            }
            Error = Resources.Strings.BulkAddFailed;
        }
    }
    [RelayCommand] private void Cancel() { _capture.Reject(); CloseRequested?.Invoke(this, EventArgs.Empty); }
    [RelayCommand] private async Task ReassociateAsync()
    {
        // Relinquish this offer before asking the browser for fresh, independently selected requests.
        // Never replay an expired download URL or copy another row's credentials.
        _reassociating = true;
        _capture.Reject("browser");
        Error = Strings.BulkRefreshHelp;
        if (_shell is null) return;
        foreach (var page in _links.Where(link => link.Selected && !link.Request.PrivateWindow)
            .Select(link => link.Request.Context.Referrer).Where(url => url is not null).Distinct().Take(10))
        {
            if (Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
                try { await _shell.OpenUrlAsync(uri); } catch { /* Keep source-page failures out of credential logs. */ }
        }
    }
    public void WindowClosed() => _capture.Reject();
}

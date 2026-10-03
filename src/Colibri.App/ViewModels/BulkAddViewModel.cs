using System.Collections.ObjectModel;
using Avalonia.Threading;
using Colibri.Core.Ipc;
using Colibri.Core.Engine;
using Colibri.Core.Models;
using Colibri.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Colibri.App.ViewModels;

public partial class BulkLinkViewModel(AddRequest request) : ObservableObject
{
    public AddRequest Request { get; } = request;
    public string Url => Request.FinalUrl ?? Request.Url;
    [ObservableProperty] private bool _selected = true;
}

/// <summary>Nothing starts until checked links are confirmed.</summary>
public partial class BulkAddViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly CaptureSession _capture;
    private readonly IReadOnlyList<BulkLinkViewModel> _links;
    public ObservableCollection<BulkLinkViewModel> VisibleLinks { get; } = [];
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string? _error;
    public event EventHandler? CloseRequested;
    public BulkAddViewModel(DownloadManager manager, IReadOnlyList<AddRequest> links, CaptureSession capture)
    {
        _manager = manager;
        _capture = capture;
        _links = links.Select(link => new BulkLinkViewModel(link)).ToArray();
        OnFilterChanged("");
        capture.Token.Register(() => Dispatcher.UIThread.Post(() => CloseRequested?.Invoke(this, EventArgs.Empty)));
    }
    partial void OnFilterChanged(string value)
    {
        VisibleLinks.Clear();
        foreach (var link in _links.Where(link => link.Url.Contains(value, StringComparison.OrdinalIgnoreCase))) VisibleLinks.Add(link);
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
                var items = await _manager.AddAsync(link.Url, link.Request.Context, link.Request.Context.FileName, null, _capture.Token);
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
    public void WindowClosed() => _capture.Reject();
}

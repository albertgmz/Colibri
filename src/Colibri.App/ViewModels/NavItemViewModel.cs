using Colibri.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Colibri.App.ViewModels;

/// <summary>What a navigation entry shows.</summary>
public enum NavFilter
{
    All,

    /// <summary>Unfinished downloads: queued, active and paused.</summary>
    Active,
    Completed,
    Failed,
    Category,
    Queue,

    /// <summary>A section title, not selectable.</summary>
    Header,
}

/// <summary>One entry of the navigation pane, with the number of downloads it shows.</summary>
public partial class NavItemViewModel : ObservableObject
{
    [ObservableProperty]
    private int _count;

    public NavItemViewModel(string label, string iconKey, NavFilter filter, DownloadCategory? category = null, Guid? queueId = null)
    {
        Label = label;
        IconKey = iconKey;
        Filter = filter;
        Category = category;
        QueueId = queueId;
    }

    public string Label { get; }

    public string IconKey { get; }

    public NavFilter Filter { get; }

    public DownloadCategory? Category { get; }
    public Guid? QueueId { get; }

    public bool IsHeader => Filter == NavFilter.Header;

    public bool IsSelectable => !IsHeader;

    public bool Matches(DownloadItemViewModel item) => Filter switch
    {
        NavFilter.All => true,
        NavFilter.Active => item.State is DownloadState.Queued or DownloadState.Active or DownloadState.Paused,
        NavFilter.Completed => item.State == DownloadState.Completed,
        NavFilter.Failed => item.State == DownloadState.Failed,
        NavFilter.Category => item.Category == Category,
        NavFilter.Queue => item.QueueId == QueueId,
        _ => false,
    };
}

using System.Globalization;
using Colibri.App.Formatting;
using Colibri.App.Resources;
using Colibri.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Colibri.App.ViewModels;

/// <summary>
/// One row of the download table. Created once per download and updated in place; each property raises
/// PropertyChanged only when its value really changes, so a poll tick redraws only what moved.
/// </summary>
public partial class DownloadItemViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePath))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePath))]
    private string _saveFolder = string.Empty;

    [ObservableProperty]
    private DownloadCategory _category;

    [ObservableProperty]
    private DownloadState _state;

    [ObservableProperty]
    private long? _totalBytes;

    [ObservableProperty]
    private long _completedBytes;

    [ObservableProperty]
    private long _speed;

    [ObservableProperty]
    private DateTimeOffset _addedAt;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _connections;

    /// <summary>The engine's piece bitfield, for the segment bar; null when unknown.</summary>
    [ObservableProperty]
    private string? _bitfield;

    [ObservableProperty]
    private int _numPieces;

    // Display values. Computed here instead of with converters so they change only when the text changes.

    [ObservableProperty]
    private string _iconKey = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _timeLeftText = string.Empty;

    /// <summary>Seconds left, for sorting the "Time left" column; unknown sorts last.</summary>
    [ObservableProperty]
    private long _secondsLeft = long.MaxValue;

    [ObservableProperty]
    private string _addedText = string.Empty;

    // Details pane texts.

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _downloadedText = string.Empty;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _categoryText = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _completedAt;

    [ObservableProperty]
    private string _completedText = string.Empty;

    [ObservableProperty]
    private double? _averageSpeed;

    [ObservableProperty]
    private string _averageSpeedText = string.Empty;

    public DownloadItemViewModel(DownloadItem item)
    {
        Id = item.Id;
        Update(item);
    }

    public Guid Id { get; }

    public string FilePath => Path.Combine(SaveFolder, FileName);

    /// <summary>Copies a fresh snapshot from the download manager into this row.</summary>
    public void Update(DownloadItem item)
    {
        FileName = item.FileName;
        Url = item.Url;
        SaveFolder = item.SaveFolder;
        Category = item.Category;
        State = item.State;
        TotalBytes = item.TotalBytes;
        CompletedBytes = item.CompletedBytes;
        Speed = item.DownloadSpeed;
        AddedAt = item.AddedAt;
        ErrorMessage = item.ErrorMessage;
        Connections = item.Connections;
        Bitfield = item.Bitfield;
        NumPieces = item.NumPieces ?? 0;
        Host = Uri.TryCreate(item.FinalUrl ?? item.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        CompletedAt = item.CompletedAt;
        CompletedText = item.CompletedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;
        AverageSpeed = item.AverageDownloadSpeed;
        AverageSpeedText = item.AverageDownloadSpeed is { } average ? DisplayFormat.Speed((long)average) : Strings.DetailsUnknown;
        CategoryText = item.Category switch
        {
            DownloadCategory.Compressed => Strings.CategoryCompressed,
            DownloadCategory.Documents => Strings.CategoryDocuments,
            DownloadCategory.Music => Strings.CategoryMusic,
            DownloadCategory.Programs => Strings.CategoryPrograms,
            DownloadCategory.Video => Strings.CategoryVideo,
            _ => Strings.CategoryOther,
        };

        IconKey = "IconCategory" + item.Category;
        SizeText = DisplayFormat.Size(item.TotalBytes);
        AddedText = item.AddedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

        var percent = item.State == DownloadState.Completed ? 100
            : item.TotalBytes is > 0 and var total ? Math.Clamp(item.CompletedBytes * 100.0 / total, 0, 100)
            : 0;
        ProgressValue = Math.Round(percent, 1);
        ProgressText = item.State switch
        {
            DownloadState.Active => item.TotalBytes is > 0 ? DisplayFormat.Percent(ProgressValue) : DisplayFormat.Size(item.CompletedBytes),
            DownloadState.Queued => Strings.StateQueued,
            DownloadState.Paused => string.Format(CultureInfo.CurrentCulture, Strings.ProgressWithStateFormat, DisplayFormat.Percent(ProgressValue), Strings.StatePaused),
            DownloadState.Completed => Strings.StateCompleted,
            _ => Strings.StateFailed,
        };

        StatusText = item.State switch
        {
            DownloadState.Active => Strings.StateDownloading,
            DownloadState.Queued => Strings.StateQueued,
            DownloadState.Paused => Strings.StatePaused,
            DownloadState.Completed => Strings.StateCompleted,
            _ when string.IsNullOrWhiteSpace(item.ErrorMessage) => Strings.StateFailed,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.StatusFailedFormat, item.ErrorMessage),
        };
        var completedBytes = item.State == DownloadState.Completed && item.TotalBytes is { } size ? size : item.CompletedBytes;
        DownloadedText = item.TotalBytes is > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.DetailsDownloadedFormat,
                DisplayFormat.Size(completedBytes), DisplayFormat.Size(item.TotalBytes), DisplayFormat.Percent(ProgressValue))
            : DisplayFormat.Size(completedBytes);

        var active = item.State == DownloadState.Active;
        var remaining = active ? DisplayFormat.Remaining(item.TotalBytes, item.CompletedBytes, item.DownloadSpeed) : null;
        SpeedText = active ? DisplayFormat.Speed(item.DownloadSpeed) : string.Empty;
        TimeLeftText = active ? DisplayFormat.TimeLeft(remaining) : string.Empty;
        SecondsLeft = remaining is { } time ? (long)time.TotalSeconds : long.MaxValue;
    }
}

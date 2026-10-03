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
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
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

        var active = item.State == DownloadState.Active;
        var remaining = active ? DisplayFormat.Remaining(item.TotalBytes, item.CompletedBytes, item.DownloadSpeed) : null;
        SpeedText = active ? DisplayFormat.Speed(item.DownloadSpeed) : string.Empty;
        TimeLeftText = active ? DisplayFormat.TimeLeft(remaining) : string.Empty;
        SecondsLeft = remaining is { } time ? (long)time.TotalSeconds : long.MaxValue;
    }
}

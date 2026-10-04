using System.Globalization;
using Colibri.App.Formatting;
using Colibri.App.Resources;
using Colibri.Core.Models;
using Colibri.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Colibri.App.ViewModels;

public partial class DownloadDetailsViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private bool _refreshing;
    private bool _optionsLoaded;
    public DownloadDetailsViewModel(DownloadManager manager, DownloadItemViewModel row)
    {
        _manager = manager;
        Row = row;
        CurrentSpeed = DisplayFormat.Speed(row.Speed);
        LastError = row.ErrorMessage ?? Strings.DetailsNone;
    }

    public DownloadItemViewModel Row { get; }
    [ObservableProperty] private string _finalUrl = Strings.DetailsUnknown;
    [ObservableProperty] private string _averageSpeed = Strings.DetailsUnknown;
    [ObservableProperty] private string _pieceSummary = Strings.DetailsUnknown;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private string _lastError = Strings.DetailsNone;
    [ObservableProperty] private string _currentSpeed = string.Empty;
    [ObservableProperty] private string _timeLeft = Strings.DetailsUnknown;
    [ObservableProperty] private IReadOnlyList<ServerRow> _servers = [];
    [ObservableProperty] private IReadOnlyList<string> _events = [];
    [ObservableProperty] private IReadOnlyList<DownloadSpeedSample> _speedHistory = [];
    [ObservableProperty] private string _graphMaximum = string.Empty;
    [ObservableProperty] private decimal? _speedLimitKiB;
    [ObservableProperty] private decimal? _connectionLimit;
    [ObservableProperty] private bool _canEditOptions;

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var details = await _manager.GetDetailsAsync(Row.Id, CancellationToken.None);
            if (details is null) { Error = Strings.DetailsRemoved; CanEditOptions = false; return; }
            var item = details.Item;
            FinalUrl = item.FinalUrl ?? Strings.DetailsUnknown;
            AverageSpeed = details.AverageBytesPerSecond is { } average ? DisplayFormat.Speed((long)average) : Strings.DetailsUnknown;
            CurrentSpeed = DisplayFormat.Speed(item.DownloadSpeed);
            TimeLeft = DisplayFormat.Remaining(item.TotalBytes, item.CompletedBytes, item.DownloadSpeed) is { } remaining
                && item.State == DownloadState.Active ? DisplayFormat.TimeLeft(remaining) : Strings.DetailsUnknown;
            LastError = item.ErrorMessage ?? Strings.DetailsNone;
            var completed = PieceMap.CompletedCount(item.Bitfield, item.NumPieces);
            PieceSummary = completed is { } count ? string.Format(CultureInfo.CurrentCulture, Strings.DetailsPieceSummary,
                item.NumPieces, count, item.NumPieces - count, DisplayFormat.Size(item.PieceLength)) : Strings.DetailsPiecesUnknown;
            Servers = details.Servers.Select(s => new ServerRow(s.FileIndex, string.IsNullOrWhiteSpace(s.Host) ? Strings.DetailsUnknown : s.Host,
                DisplayFormat.Speed(s.BytesPerSecond))).ToArray();
            SpeedHistory = details.SpeedHistory;
            GraphMaximum = DisplayFormat.Speed(details.SpeedHistory.Count > 0 ? details.SpeedHistory.Max(s => s.BytesPerSecond) : 0);
            Events = details.Events.Select(e => $"{e.Timestamp.ToLocalTime():T}  {EventText(e.Kind)}{(e.Message is { } message ? " · " + message : string.Empty)}").ToArray();
            Error = details.EngineDetailsError ?? string.Empty;
            CanEditOptions = details.Options is not null && item.State != DownloadState.Completed;
            if (!_optionsLoaded && details.Options is { } options)
            {
                SpeedLimitKiB = options.SpeedLimitBytesPerSecond / 1024m;
                ConnectionLimit = options.ConnectionsPerServer;
                _optionsLoaded = true;
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { _refreshing = false; }
    }

    [RelayCommand]
    private async Task ApplyOptionsAsync()
    {
        if (SpeedLimitKiB is not { } speed || speed < 0 || speed > 10_000_000 || ConnectionLimit is not { } count
            || count < 1 || count > 16 || count != decimal.Truncate(count)) { Error = Strings.DetailsOptionsInvalid; return; }
        try
        {
            await _manager.ApplyDownloadOptionsAsync(Row.Id, new((long)(speed * 1024), (int)count), CancellationToken.None);
            _optionsLoaded = false;
            await RefreshAsync();
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private static string EventText(DownloadLogKind kind) => kind switch
    {
        DownloadLogKind.Started => Strings.DetailLogStarted,
        DownloadLogKind.Queued => Strings.StateQueued,
        DownloadLogKind.Paused => Strings.StatePaused,
        DownloadLogKind.Retried => Strings.DetailLogRetried,
        DownloadLogKind.Completed => Strings.StateCompleted,
        DownloadLogKind.Failed => Strings.StateFailed,
        DownloadLogKind.Redirected => Strings.DetailLogRedirected,
        _ => Strings.DetailLogObserved,
    };

}

public sealed record ServerRow(int FileIndex, string Host, string Speed);

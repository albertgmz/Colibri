using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Colibri.App.Resources;
using Colibri.Core.Queues;
using Colibri.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Colibri.App.ViewModels;

public partial class QueueManagementViewModel : ObservableObject, IDisposable
{
    private readonly DownloadManager _manager;
    public ObservableCollection<DownloadQueue> Queues { get; } = [];
    public IReadOnlyList<string> ScheduleModes { get; } = [Strings.QueueScheduleNone, Strings.QueueScheduleOnce, Strings.QueueScheduleWeekly];
    public IReadOnlyList<string> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones().Select(zone => zone.Id).ToArray();

    [ObservableProperty] private DownloadQueue? _selectedQueue;
    [ObservableProperty] private string _queueName = "";
    [ObservableProperty] private int _concurrency = 3;
    [ObservableProperty] private int _scheduleMode;
    [ObservableProperty] private string _startAt = "";
    [ObservableProperty] private string _stopAt = "";
    [ObservableProperty] private string _timeZoneId = TimeZoneInfo.Local.Id;
    [ObservableProperty] private string _startTime = "09:00";
    [ObservableProperty] private string _stopTime = "17:00";
    [ObservableProperty] private string _days = "1,2,3,4,5";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _status = "";

    public bool IsOnce => ScheduleMode == 1;
    public bool IsWeekly => ScheduleMode == 2;
    partial void OnScheduleModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsOnce));
        OnPropertyChanged(nameof(IsWeekly));
    }

    public QueueManagementViewModel(DownloadManager manager)
    {
        _manager = manager;
        _manager.QueuesChanged += OnQueuesChanged;
    }

    public async Task LoadAsync(CancellationToken ct) => Refresh(await _manager.GetQueuesAsync(ct));
    private void OnQueuesChanged(object? sender, IReadOnlyList<DownloadQueue> queues) => Dispatcher.UIThread.Post(() => Refresh(queues));
    private void Refresh(IReadOnlyList<DownloadQueue> queues)
    {
        var id = SelectedQueue?.Id;
        Queues.Clear();
        foreach (var queue in queues.OrderBy(q => q.Id != Guid.Empty).ThenBy(q => q.Name)) Queues.Add(queue);
        SelectedQueue = Queues.FirstOrDefault(q => q.Id == id) ?? Queues.FirstOrDefault();
    }

    partial void OnSelectedQueueChanged(DownloadQueue? value)
    {
        if (value is null) return;
        QueueName = value.Name;
        Concurrency = value.MaxConcurrentDownloads;
        Status = value.IsRunning ? Strings.QueueStatusRunning : Strings.QueueStatusStopped;
        var schedule = value.Schedule;
        ScheduleMode = schedule is null ? 0 : schedule.Days.Length > 0 ? 2 : 1;
        StartAt = schedule?.StartAt?.ToString("O") ?? "";
        StopAt = schedule?.StopAt?.ToString("O") ?? "";
        TimeZoneId = schedule?.TimeZoneId ?? TimeZoneInfo.Local.Id;
        StartTime = schedule?.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00";
        StopTime = schedule?.StopTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "17:00";
        Days = schedule is null ? "1,2,3,4,5" : string.Join(',', schedule.Days.Select(day => (int)day));
    }

    [RelayCommand] private Task CreateAsync() => ExecuteAsync(async () =>
    {
        var queue = await _manager.CreateQueueAsync(QueueName, Concurrency, CancellationToken.None);
        Refresh(await _manager.GetQueuesAsync(CancellationToken.None));
        SelectedQueue = Queues.First(q => q.Id == queue.Id);
    });
    [RelayCommand] private Task SaveAsync() => ExecuteAsync(async () =>
    {
        if (SelectedQueue is not { } queue) return;
        QueueSchedule? schedule = ScheduleMode switch
        {
            1 => new QueueSchedule { StartAt = ParseDate(StartAt), StopAt = ParseDate(StopAt) },
            2 => new QueueSchedule
            {
                TimeZoneId = TimeZoneId,
                StartTime = TimeOnly.ParseExact(StartTime, "HH:mm", CultureInfo.InvariantCulture),
                StopTime = TimeOnly.ParseExact(StopTime, "HH:mm", CultureInfo.InvariantCulture),
                Days = Days.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(day => (DayOfWeek)int.Parse(day, CultureInfo.InvariantCulture)).Distinct().ToArray()
            },
            _ => null
        };
        if (ScheduleMode == 1 && schedule?.StartAt is null && schedule?.StopAt is null)
            throw new ArgumentException(Strings.QueueSchedule);
        await _manager.UpdateQueueAsync(queue with { Name = QueueName, MaxConcurrentDownloads = Concurrency, Schedule = schedule }, CancellationToken.None);
    });
    private static DateTimeOffset? ParseDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Requiring the round-trip representation keeps the offset explicit across machines.
        if (!DateTimeOffset.TryParseExact(text.Trim(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new FormatException(Strings.QueueStartAt);
        return value;
    }
    [RelayCommand] private Task StartAsync() => ExecuteAsync(() => SelectedQueue is { } queue
        ? _manager.SetQueueRunningAsync(queue.Id, true, CancellationToken.None) : Task.CompletedTask);
    [RelayCommand] private Task StopAsync() => ExecuteAsync(() => SelectedQueue is { } queue
        ? _manager.SetQueueRunningAsync(queue.Id, false, CancellationToken.None) : Task.CompletedTask);
    [RelayCommand] private Task DeleteAsync() => ExecuteAsync(() => SelectedQueue is { } queue
        ? _manager.DeleteQueueAsync(queue.Id, CancellationToken.None) : Task.CompletedTask);

    private async Task ExecuteAsync(Func<Task> action)
    {
        Error = null;
        try { await action(); }
        catch (Exception ex) { Error = ex.Message; }
    }
    public void Dispose() => _manager.QueuesChanged -= OnQueuesChanged;
}

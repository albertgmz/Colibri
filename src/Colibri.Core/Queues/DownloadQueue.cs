using System.Text.Json;
using Colibri.Core.Platform;

namespace Colibri.Core.Queues;

public sealed record DownloadQueue
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "Default";
    public int MaxConcurrentDownloads { get; init; } = 3;
    public bool IsRunning { get; init; } = true;
    public QueueSchedule? Schedule { get; init; }
    public DateTimeOffset LastScheduleEvaluationUtc { get; init; }
}

public sealed record QueueSchedule
{
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset? StopAt { get; init; }
    public string TimeZoneId { get; init; } = TimeZoneInfo.Local.Id;
    public DayOfWeek[] Days { get; init; } = [];
    public TimeOnly? StartTime { get; init; }
    public TimeOnly? StopTime { get; init; }

    public void Validate()
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        if (StartAt is { } start && StopAt is { } stop && stop <= start)
            throw new ArgumentException("The stop must be after the start.");
        if (Days.Any(day => !Enum.IsDefined(day))) throw new ArgumentException("Invalid schedule day.");
        if (Days.Length > 0 && (StartTime is null || StopTime is null))
            throw new ArgumentException("Recurring schedules need start and stop times.");
        if (Days.Length == 0 && (StartTime is not null || StopTime is not null))
            throw new ArgumentException("Choose at least one recurring day.");
    }
}

/// <summary>Atomic per-user queue configuration; a failed save never replaces the previous file.</summary>
internal sealed class QueueStore(IAppPaths paths)
{
    private readonly string _path = Path.Combine(paths.DataDirectory, "queues.json");
    public async Task<List<DownloadQueue>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<DownloadQueue>>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("The queue file is empty.");
    }

    public async Task SaveAsync(IEnumerable<DownloadQueue> queues, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, queues.ToArray(), cancellationToken: ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

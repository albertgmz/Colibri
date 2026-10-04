namespace Colibri.Core.Queues;

/// <summary>Evaluates wall-clock schedule edges. Fall-back times fire once, at their first occurrence;
/// nonexistent spring-forward times move forward to the next valid minute.</summary>
public static class QueueScheduleEvaluator
{
    public static bool? GetRunningState(QueueSchedule schedule, DateTimeOffset after, DateTimeOffset now)
    {
        if (now <= after) return null; // Clock rollback must not replay an already processed edge.
        var edges = new List<(DateTimeOffset At, bool Running)>();
        if (schedule.StartAt is { } start) edges.Add((start, true));
        if (schedule.StopAt is { } stop) edges.Add((stop, false));
        if (schedule.Days.Length > 0 && schedule.StartTime is { } startTime && schedule.StopTime is { } stopTime)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            // A weekly schedule's most recent edge is always in this interval, even after long downtime.
            for (var offset = -8; offset <= 0; offset++)
            {
                var date = today.AddDays(offset);
                if (!schedule.Days.Contains(date.DayOfWeek)) continue;
                edges.Add((ToUtc(date, startTime, zone), true));
                var stopDate = stopTime <= startTime ? date.AddDays(1) : date;
                edges.Add((ToUtc(stopDate, stopTime, zone), false));
            }
        }
        var due = edges.Where(edge => edge.At > after && edge.At <= now)
            .OrderBy(edge => edge.At).ThenBy(edge => edge.Running).ToList();
        return due.Count == 0 ? null : due[^1].Running;
    }

    internal static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime();
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}

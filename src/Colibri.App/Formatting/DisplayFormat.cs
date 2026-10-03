using System.Globalization;
using Colibri.App.Resources;

namespace Colibri.App.Formatting;

/// <summary>
/// Turns sizes, speeds and remaining times into the short texts shown in the table and status bar.
/// Numbers use the current culture; words come from <see cref="Strings"/>.
/// </summary>
public static class DisplayFormat
{
    private static readonly Func<string>[] SizeUnits =
    [
        () => Strings.SizeKilobytesFormat,
        () => Strings.SizeMegabytesFormat,
        () => Strings.SizeGigabytesFormat,
        () => Strings.SizeTerabytesFormat,
    ];

    /// <summary>"512 B", "1.5 KB", "700.0 MB" (1 KB = 1024 bytes); "Unknown" when null.</summary>
    public static string Size(long? bytes)
    {
        if (bytes is not { } value || value < 0)
        {
            return Strings.SizeUnknown;
        }

        if (value < 1024)
        {
            return Format(Strings.SizeBytesFormat, value);
        }

        double scaled = value;
        var unit = -1;
        while (scaled >= 1024 && unit < SizeUnits.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return Format(SizeUnits[unit](), scaled.ToString("N1", CultureInfo.CurrentCulture));
    }

    /// <summary>"1.5 MB/s".</summary>
    public static string Speed(long bytesPerSecond) => Format(Strings.SpeedFormat, Size(Math.Max(0, bytesPerSecond)));

    /// <summary>"45.3 %".</summary>
    public static string Percent(double percent) => Format(Strings.PercentFormat, percent);

    /// <summary>Time to finish at the current speed, or null when the size or the speed is unknown.</summary>
    public static TimeSpan? Remaining(long? totalBytes, long completedBytes, long bytesPerSecond)
    {
        if (totalBytes is not { } total || total <= 0 || bytesPerSecond <= 0)
        {
            return null;
        }

        var seconds = Math.Ceiling(Math.Max(0, total - completedBytes) / (double)bytesPerSecond);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>"45 s", "2 min 5 s", "1 h 20 min", "3 d 4 h"; "—" when unknown.</summary>
    public static string TimeLeft(TimeSpan? remaining)
    {
        if (remaining is not { } time || time < TimeSpan.Zero)
        {
            return Strings.EtaUnknown;
        }

        // Rounded up: "0 s" while bytes are still missing would be misleading.
        var total = (long)Math.Ceiling(time.TotalSeconds);
        if (total < 60)
        {
            return Format(Strings.EtaSecondsFormat, total);
        }

        if (total < 3600)
        {
            return Format(Strings.EtaMinutesSecondsFormat, total / 60, total % 60);
        }

        if (total < 86400)
        {
            return Format(Strings.EtaHoursMinutesFormat, total / 3600, total % 3600 / 60);
        }

        return Format(Strings.EtaDaysHoursFormat, total / 86400, total % 86400 / 3600);
    }

    private static string Format(string format, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);
}

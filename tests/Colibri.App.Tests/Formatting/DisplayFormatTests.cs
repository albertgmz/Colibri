using System.Globalization;
using Colibri.App.Formatting;

namespace Colibri.App.Tests.Formatting;

public sealed class DisplayFormatTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    public DisplayFormatTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1_048_576L, "1.0 MB")]
    [InlineData(734_003_200L, "700.0 MB")]
    [InlineData(5_368_709_120L, "5.0 GB")]
    [InlineData(1_099_511_627_776L, "1.0 TB")]
    public void Sizes_use_binary_units_with_one_decimal(long bytes, string expected)
    {
        Assert.Equal(expected, DisplayFormat.Size(bytes));
    }

    [Fact]
    public void Unknown_size_is_shown_as_unknown()
    {
        Assert.Equal("Unknown", DisplayFormat.Size(null));
    }

    [Fact]
    public void Sizes_follow_the_current_culture()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

        Assert.Equal("1,5 KB", DisplayFormat.Size(1536));
    }

    [Fact]
    public void Speed_is_a_size_per_second()
    {
        Assert.Equal("2.5 MB/s", DisplayFormat.Speed(2_621_440));
        Assert.Equal("0 B/s", DisplayFormat.Speed(0));
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(125, "2 min 5 s")]
    [InlineData(3599, "59 min 59 s")]
    [InlineData(4800, "1 h 20 min")]
    [InlineData(273_600, "3 d 4 h")]
    public void Time_left_is_short_and_readable(int seconds, string expected)
    {
        Assert.Equal(expected, DisplayFormat.TimeLeft(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Time_left_rounds_partial_seconds_up()
    {
        Assert.Equal("1 s", DisplayFormat.TimeLeft(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Unknown_time_left_is_a_dash()
    {
        Assert.Equal("—", DisplayFormat.TimeLeft(null));
    }

    [Theory]
    [InlineData(null, 0L, 100L)]
    [InlineData(1000L, 0L, 0L)]
    [InlineData(0L, 0L, 100L)]
    public void Remaining_is_unknown_without_a_size_or_a_speed(long? total, long completed, long speed)
    {
        Assert.Null(DisplayFormat.Remaining(total, completed, speed));
    }

    [Fact]
    public void Remaining_is_the_missing_bytes_divided_by_the_speed()
    {
        Assert.Equal(TimeSpan.FromSeconds(9), DisplayFormat.Remaining(1000, 100, 100));
        Assert.Equal(TimeSpan.FromSeconds(1), DisplayFormat.Remaining(1000, 950, 100));
    }

    [Fact]
    public void Percent_has_one_decimal()
    {
        Assert.Equal("45.3 %", DisplayFormat.Percent(45.25));
    }
}

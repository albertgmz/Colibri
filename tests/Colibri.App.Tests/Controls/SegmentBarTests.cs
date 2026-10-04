using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Colibri.App.Controls;

namespace Colibri.App.Tests.Controls;

public class SegmentBarTests
{
    [Theory]
    [InlineData("f0", 8, new[] { 0, 4 })]
    [InlineData("a0", 3, new[] { 0, 1, 2, 1 })]
    [InlineData("ff", 6, new[] { 0, 6 })] // Padding bits past the last piece are ignored.
    [InlineData("8001", 16, new[] { 0, 1, 15, 1 })]
    [InlineData("0F", 8, new[] { 4, 4 })] // Upper case hex.
    [InlineData("ffff", 32, new[] { 0, 16 })] // Missing hex digits mean "not downloaded".
    [InlineData("00", 8, new int[0])]
    public void Bitfield_becomes_runs_with_the_highest_bit_of_the_first_byte_as_piece_zero(string bitfield, int pieces, int[] expected)
    {
        var runs = SegmentRuns.FromBitfield(bitfield, pieces);

        Assert.NotNull(runs);
        Assert.Equal(expected, runs.SelectMany(r => new[] { r.Start, r.Count }));
    }

    [Theory]
    [InlineData(null, 8)]
    [InlineData("", 8)]
    [InlineData("fg", 8)] // Not hex: nothing is trusted.
    [InlineData("ff", 0)]
    public void No_usable_bitfield_gives_no_runs(string? bitfield, int pieces)
    {
        Assert.Null(SegmentRuns.FromBitfield(bitfield, pieces));
    }

    [Fact]
    public void Thousands_of_pieces_merge_into_a_few_runs()
    {
        // 4000 pieces: the first half done, then every other byte done.
        var bitfield = new string('f', 500) + string.Concat(Enumerable.Repeat("ff00", 125));

        var runs = SegmentRuns.FromBitfield(bitfield, 4000)!;

        Assert.Equal(125, runs.Count); // The first "ff" joins the finished first half.
        Assert.Equal(new PieceRun(0, 2008), runs[0]);
    }

    [AvaloniaFact]
    public void Renders_one_rectangle_per_run_scaled_to_the_width()
    {
        var bar = new SegmentBar { Bitfield = "f00f", PieceCount = 16, Fill = Brushes.Blue, Track = Brushes.Gray, Height = 10, Width = 160 };
        var window = new Window { Content = bar, Width = 200, Height = 50 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rectangles = bar.FilledRectangles(new Size(160, 10));

        Assert.Equal([new Rect(0, 0, 40, 10), new Rect(120, 0, 40, 10)], rectangles);
        Assert.Equal(160, bar.Bounds.Width);
        window.Close();
    }

    [AvaloniaFact]
    public void Without_a_bitfield_it_draws_a_proportional_bar()
    {
        var bar = new SegmentBar { Progress = 25 };

        Assert.Equal([new Rect(0, 0, 50, 8)], bar.FilledRectangles(new Size(200, 8)));

        bar.Progress = 0;
        Assert.Empty(bar.FilledRectangles(new Size(200, 8)));
    }

    [AvaloniaFact]
    public void An_invalid_bitfield_falls_back_to_the_proportional_bar()
    {
        var bar = new SegmentBar { Bitfield = "zz", PieceCount = 8, Progress = 50 };

        Assert.Equal([new Rect(0, 0, 100, 8)], bar.FilledRectangles(new Size(200, 8)));
    }
}

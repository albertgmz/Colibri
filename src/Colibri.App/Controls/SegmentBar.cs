using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Colibri.App.Controls;

/// <summary>
/// Shows which parts of a file are downloaded: each run of downloaded pieces from the engine's bitfield
/// is drawn as one filled rectangle. Without a valid bitfield it draws a plain bar from <see cref="Progress"/>.
/// </summary>
public class SegmentBar : Control
{
    public static readonly StyledProperty<string?> BitfieldProperty =
        AvaloniaProperty.Register<SegmentBar, string?>(nameof(Bitfield));

    public static readonly StyledProperty<int> PieceCountProperty =
        AvaloniaProperty.Register<SegmentBar, int>(nameof(PieceCount));

    /// <summary>Percent done (0 to 100), used when there is no bitfield.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<SegmentBar, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Track));

    // Null when there is no usable bitfield; the bar then shows Progress.
    private IReadOnlyList<PieceRun>? _runs;

    static SegmentBar()
    {
        AffectsRender<SegmentBar>(BitfieldProperty, PieceCountProperty, ProgressProperty, FillProperty, TrackProperty);
    }

    public string? Bitfield
    {
        get => GetValue(BitfieldProperty);
        set => SetValue(BitfieldProperty, value);
    }

    public int PieceCount
    {
        get => GetValue(PieceCountProperty);
        set => SetValue(PieceCountProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Parse once per change, not on every render.
        if (change.Property == BitfieldProperty || change.Property == PieceCountProperty)
        {
            _runs = SegmentRuns.FromBitfield(Bitfield, PieceCount);
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (Track is { } track)
        {
            context.FillRectangle(track, new Rect(size));
        }

        if (Fill is { } fill)
        {
            foreach (var rectangle in FilledRectangles(size))
            {
                context.FillRectangle(fill, rectangle);
            }
        }
    }

    /// <summary>The rectangles to fill for a bar of <paramref name="size"/>: one per run, or one for the plain bar.</summary>
    internal IReadOnlyList<Rect> FilledRectangles(Size size)
    {
        if (_runs is null)
        {
            var done = Math.Clamp(Progress, 0, 100) / 100 * size.Width;
            return done > 0 ? [new Rect(0, 0, done, size.Height)] : [];
        }

        var pieceWidth = size.Width / PieceCount;

        // At least one pixel wide, so a single finished piece of a huge file is still visible.
        return _runs
            .Select(run => new Rect(run.Start * pieceWidth, 0, Math.Max(1, run.Count * pieceWidth), size.Height))
            .ToList();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Colibri.Core.Models;

namespace Colibri.App.Controls;

/// <summary>Uses actual observation timestamps; gaps keep their elapsed width.</summary>
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<DownloadSpeedSample>?> SamplesProperty =
        AvaloniaProperty.Register<SpeedGraph, IReadOnlyList<DownloadSpeedSample>?>(nameof(Samples));
    public IReadOnlyList<DownloadSpeedSample>? Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    static SpeedGraph() => AffectsRender<SpeedGraph>(SamplesProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(null, new Pen(Brushes.Gray, 1), bounds);
        if (Samples is not { Count: > 1 } samples) return;
        var end = samples[^1].Timestamp;
        var start = end.AddMinutes(-5);
        var maximum = Math.Max(1, samples.Max(s => s.BytesPerSecond));
        var pen = new Pen(Brushes.IndianRed, 2);
        Point? previous = null;
        foreach (var sample in samples)
        {
            var point = new Point(Math.Clamp((sample.Timestamp - start).TotalSeconds / 300, 0, 1) * bounds.Width,
                bounds.Height - Math.Clamp(sample.BytesPerSecond / (double)maximum, 0, 1) * bounds.Height);
            if (previous is { } from) context.DrawLine(pen, from, point);
            previous = point;
        }
    }
}

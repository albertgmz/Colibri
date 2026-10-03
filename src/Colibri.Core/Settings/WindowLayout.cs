namespace Colibri.Core.Settings;

public enum LayoutDensity { Compact, Comfortable }
public enum ToolbarMode { Labels, Icons, SmallIcons }

/// <summary>UI-independent layout preferences. Missing v1 fields use these defaults.</summary>
public sealed class WindowLayout
{
    public double Width { get; set; } = 960;
    public double Height { get; set; } = 600;
    // Positions are physical screen pixels; dimensions are device-independent pixels.
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Maximized { get; set; }
    public double SidebarWidth { get; set; } = 160;
    public double DetailsHeight { get; set; } = 180;
    public bool SidebarCollapsed { get; set; }
    public double HeaderHeight { get; set; } = 28;
    public LayoutDensity Density { get; set; }
    public ToolbarMode Toolbar { get; set; } = ToolbarMode.Labels;
    public List<ColumnLayout> Columns { get; set; } = [];
    public List<ColumnSort> Sort { get; set; } = [];

    /// <summary>Keep hand-edited settings from making a window unusable.</summary>
    public void Normalize()
    {
        Width = Bound(Width, 640, 7680, 960);
        Height = Bound(Height, 400, 4320, 600);
        SidebarWidth = Bound(SidebarWidth, 120, 400, 160);
        DetailsHeight = Bound(DetailsHeight, 100, 1200, 180);
        HeaderHeight = Bound(HeaderHeight, 24, 72, 28);
        if (!Enum.IsDefined(Density)) Density = LayoutDensity.Compact;
        if (!Enum.IsDefined(Toolbar)) Toolbar = ToolbarMode.Labels;
        Columns ??= [];
        Sort ??= [];
        Columns = Columns.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Key))
            .DistinctBy(c => c.Key).Take(64).ToList();
        foreach (var column in Columns)
        {
            column.Width = column.Star ? Bound(column.Width, 0.1, 1000, 1) : Bound(column.Width, 40, 2000, 100);
            column.Order = Math.Clamp(column.Order, 0, 63);
        }
        Sort = Sort.Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Key))
            .DistinctBy(s => s.Key).Take(16).ToList();
    }

    private static double Bound(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed class ColumnLayout
{
    public string Key { get; set; } = string.Empty;
    public double Width { get; set; } = 100;
    public bool Star { get; set; }
    public int Order { get; set; }
    public bool Visible { get; set; } = true;
}

public sealed class ColumnSort
{
    public string Key { get; set; } = string.Empty;
    public bool Descending { get; set; }
}

using System.Text.Json;
using Colibri.Core.Settings;

namespace Colibri.Core.Tests;

public class WindowLayoutTests
{
    [Fact]
    public void V1_settings_keep_existing_preferences_and_get_compact_layout_defaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"ConnectionsPerServer":16,"ShowDetailsPane":false,"DefaultDownloadFolder":"D:/files"}""")!;
        Assert.Equal(16, settings.ConnectionsPerServer);
        Assert.False(settings.ShowDetailsPane);
        Assert.Equal("D:/files", settings.DefaultDownloadFolder);
        Assert.Equal(960, settings.Layout.Width);
        Assert.Equal(LayoutDensity.Compact, settings.Layout.Density);
    }

    [Fact]
    public void Layout_survives_json_roundtrip_including_hidden_columns_and_sort()
    {
        var layout = new WindowLayout
        {
            Width = 800, Height = 500, X = -100, Y = 40, SidebarWidth = 190, DetailsHeight = 170,
            SidebarCollapsed = true, Toolbar = ToolbarMode.SmallIcons, Density = LayoutDensity.Comfortable,
            Columns = [new() { Key = "Speed", Width = 135, Order = 2, Visible = false }],
            Sort = [new() { Key = "Speed", Descending = true }],
        };
        var copy = JsonSerializer.Deserialize<WindowLayout>(JsonSerializer.Serialize(layout))!;
        Assert.Equal(800, copy.Width);
        Assert.Equal(-100, copy.X);
        Assert.Equal(190, copy.SidebarWidth);
        Assert.True(copy.SidebarCollapsed);
        Assert.Equal(ToolbarMode.SmallIcons, copy.Toolbar);
        Assert.False(Assert.Single(copy.Columns).Visible);
        Assert.True(Assert.Single(copy.Sort).Descending);
    }

    [Fact]
    public void Invalid_geometry_and_null_collections_are_normalized()
    {
        var layout = new WindowLayout
        {
            Width = double.NaN, Height = -1, SidebarWidth = double.PositiveInfinity,
            DetailsHeight = 1, Columns = null!, Sort = null!, Density = (LayoutDensity)99,
        };
        layout.Normalize();
        Assert.Equal(960, layout.Width);
        Assert.Equal(400, layout.Height);
        Assert.Equal(160, layout.SidebarWidth);
        Assert.Equal(100, layout.DetailsHeight);
        Assert.Empty(layout.Columns);
        Assert.Empty(layout.Sort);
        Assert.Equal(LayoutDensity.Compact, layout.Density);
    }
}

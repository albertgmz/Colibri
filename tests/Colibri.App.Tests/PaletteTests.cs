using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Colibri.App.Resources;
using Colibri.App.Services;
using Colibri.App.Views;
using Colibri.Core.Settings;

namespace Colibri.App.Tests;

public class PaletteTests
{
    private static Color Brush(IBrush? value) => Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;

    [AvaloniaFact]
    public async Task Palette_selection_loads_without_saving_and_preserves_page_mode_and_accent()
    {
        await using var ui = await UiHarness.StartAsync();
        ui.Settings.BackgroundPalette = " OCEAN ";
        ui.Settings.Theme = AppTheme.Dark;
        ui.Settings.AccentColor = "#0078D4";
        await ui.SettingsPage.LoadAsync();
        Assert.Equal(2, ui.SettingsPage.PaletteIndex);
        Assert.Equal(0, ui.SettingsStore.SaveCount);
        var view = new SettingsView { DataContext = ui.SettingsPage };
        var window = new Window { Width = 640, Height = 640, Content = view };
        try
        {
            window.Show();
            ui.SettingsPage.SelectedPageIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var selector = view.FindControl<ComboBox>("PaletteBox")!;
            Assert.True(selector.IsEffectivelyVisible);
            Assert.True(selector.Focus());
            for (var index = 0; index < BackgroundPalettes.Ids.Count; index++)
            {
                var before = ui.SettingsStore.SaveCount;
                ui.SettingsPage.PaletteIndex = index;
                await ui.SettingsPage.PendingWork;
                Assert.Equal(before + 1, ui.SettingsStore.SaveCount);
                Assert.Equal(BackgroundPalettes.Ids[index], ui.SettingsStore.Saved!.BackgroundPalette);
                Assert.Equal(AppTheme.Dark, ui.Settings.Theme);
                Assert.Equal("#0078D4", ui.Settings.AccentColor);
                Assert.Equal(1, ui.SettingsPage.SelectedPageIndex);
            }
            var saves = ui.SettingsStore.SaveCount;
            ui.SettingsPage.PaletteIndex = -1;
            ui.SettingsPage.PaletteIndex = 50;
            await ui.SettingsPage.PendingWork;
            Assert.Equal(saves, ui.SettingsStore.SaveCount);
        }
        finally { window.Close(); ThemeService.ApplyAppearance(AppTheme.System, "warm", "#C42B1C"); }
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public async Task Existing_windows_and_realized_Fluent_controls_follow_palette_replacement(AppTheme mode)
    {
        await using var ui = await UiHarness.StartAsync();
        ThemeService.ApplyAppearance(mode, "warm", "#0078D4");
        var main = ui.ShowWindow();
        var add = new AddUrlWindow();
        var details = new DownloadDetailsWindow();
        var selector = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 };
        var fluentWindow = new Window { Content = selector };
        var app = Application.Current!;
        var styles = app.Styles.ToArray();
        var originalDensity = app.Styles.OfType<FluentTheme>().Single().DensityStyle;
        try
        {
            add.Show(); details.Show(); fluentWindow.Show();
            selector.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            var initialPopup = selector.GetVisualDescendants().OfType<Popup>().Single(popup => popup.Name == "PART_Popup");
            var warmPopupColor = Brush(Assert.IsType<Border>(initialPopup.Child).Background);
            foreach (var id in BackgroundPalettes.Ids.Concat(new[] { "warm" }))
            {
                ThemeService.ApplyPalette(id);
                Dispatcher.UIThread.RunJobs();
                var colors = mode == AppTheme.Light ? DesignPalettes.Get(id).Light : DesignPalettes.Get(id).Dark;
                selector.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                var popup = selector.GetVisualDescendants().OfType<Popup>().Single(item => item.Name == "PART_Popup");
                var popupBorder = Assert.IsType<Border>(popup.Child);
                // Fluent popup surfaces resolve ChromeMediumLow, which actually changes for these palettes.
                var expectedPopup = id == "warm" ? warmPopupColor : Color.Parse(colors.Card);
                Assert.Equal(expectedPopup, Brush(popupBorder.Background));
                if (id != "warm" && mode == AppTheme.Dark) Assert.NotEqual(warmPopupColor, Brush(popupBorder.Background));
                foreach (var window in new Window[] { main, add, details }) Assert.Equal(Color.Parse(colors.Chrome), Brush(window.Background));
                var attached = app.Styles.OfType<FluentTheme>().Single();
                Assert.Equal(originalDensity, attached.DensityStyle);
                Assert.Equal(styles.Length, app.Styles.Count);
                for (var index = 1; index < styles.Length; index++) Assert.Same(styles[index], app.Styles[index]);
                Assert.Equal(mode == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark, app.RequestedThemeVariant);
                Assert.All(attached.Palettes.Values, palette => Assert.Equal(Color.Parse("#0078D4"), palette.Accent));
                // Compare the actual rendered control/template to a newly realized control, not just a resource lookup.
                var freshSelector = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 };
                var fresh = new Window { Content = freshSelector };
                fresh.Show(); Dispatcher.UIThread.RunJobs();
                Assert.Equal(Brush(freshSelector.Background), Brush(selector.Background));
                var rendered = selector.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "Background");
                Assert.Equal(Brush(freshSelector.Background), Brush(rendered.Background));
                fresh.Close();
                ThemeService.ApplyPalette(id);
                Assert.Same(attached, app.Styles.OfType<FluentTheme>().Single());
                var newer = new AddUrlWindow();
                newer.Show(); Dispatcher.UIThread.RunJobs();
                Assert.Equal(Color.Parse(colors.Chrome), Brush(newer.Background));
                newer.Close();
            }
        }
        finally
        {
            add.Close(); details.Close(); fluentWindow.Close();
            ThemeService.ApplyAppearance(AppTheme.System, "warm", "#C42B1C");
        }
    }

    [AvaloniaFact]
    public void System_mode_keeps_both_variants_available_to_existing_windows()
    {
        ThemeService.ApplyAppearance(AppTheme.System, "ocean", "#C77800");
        var window = new AddUrlWindow();
        try
        {
            window.Show();
            // Emulate platform variant propagation at the window boundary; real OS changes remain a runtime check.
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                var expected = variant == ThemeVariant.Dark ? "#17232E" : "#E8F0F5";
                Assert.Equal(Color.Parse(expected), Brush(window.Background));
                Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
                var fluent = Application.Current.Styles.OfType<FluentTheme>().Single();
                Assert.Equal(Color.Parse("#C77800"), fluent.Palettes[variant].Accent);
            }
        }
        finally { window.Close(); ThemeService.ApplyAppearance(AppTheme.System, "warm", "#C42B1C"); }
    }
}

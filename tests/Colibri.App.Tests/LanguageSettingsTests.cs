using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Colibri.App.Views;

namespace Colibri.App.Tests;

public class LanguageSettingsTests
{
    [AvaloniaFact]
    public async Task Language_selector_saves_once_retains_other_preferences_and_defers_culture_change()
    {
        await using var ui = await UiHarness.StartAsync();
        var culture = CultureInfo.CurrentUICulture;
        var formatting = CultureInfo.CurrentCulture;
        await ui.SettingsPage.LoadAsync();
        Assert.Equal(0, ui.SettingsStore.SaveCount);
        var view = new SettingsView { DataContext = ui.SettingsPage };
        var window = new Window { Content = view, Width = 640, Height = 640 };
        try
        {
            window.Show();
            ui.SettingsPage.SelectedPageIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var selector = view.FindControl<ComboBox>("LanguageBox")!;
            Assert.True(selector.IsEffectivelyVisible);
            Assert.True(selector.Focus());
            selector.SelectedIndex = 1;
            await ui.SettingsPage.PendingWork;
            Assert.Equal("en", ui.SettingsStore.Saved!.Language);
            Assert.Equal(1, ui.SettingsStore.SaveCount);
            Assert.True(ui.SettingsPage.IsLanguageRestartRequired);
            Assert.Same(culture, CultureInfo.CurrentUICulture);
            Assert.Same(formatting, CultureInfo.CurrentCulture);
            Assert.Equal("warm", ui.Settings.BackgroundPalette);
            Assert.Equal("#C42B1C", ui.Settings.AccentColor);
            ui.SettingsPage.SelectedPageIndex = 2;
            ui.SettingsPage.SelectedPageIndex = 1;
            await ui.SettingsPage.LoadAsync();
            Assert.Equal(1, ui.SettingsPage.LanguageIndex);
            Assert.Equal(1, ui.SettingsStore.SaveCount);
            ui.SettingsPage.LanguageIndex = -1;
            ui.SettingsPage.LanguageIndex = int.MaxValue;
            await ui.SettingsPage.PendingWork;
            Assert.Equal(1, ui.SettingsStore.SaveCount);
        }
        finally { window.Close(); }
    }
}

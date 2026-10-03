using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Colibri.Core.Models;

namespace Colibri.App.Tests;

public class DeleteConfirmationTests
{
    private static string WriteFile(DownloadItem item)
    {
        Directory.CreateDirectory(item.SaveFolder);
        var path = Path.Combine(item.SaveFolder, item.FileName);
        File.WriteAllText(path, "data");
        return path;
    }

    /// <summary>A completed download in a folder of its own, so tests never share files.</summary>
    private static DownloadItem Seed(string name)
    {
        var item = UiHarness.Item(name, DownloadState.Completed);
        item.SaveFolder = Path.Combine(Path.GetTempPath(), "colibri-ui-tests", Guid.NewGuid().ToString("N"));
        return item;
    }

    [AvaloniaFact]
    public async Task Delete_asks_first_and_cancel_keeps_everything()
    {
        var seed = Seed("keep.zip");
        var file = WriteFile(seed);
        await using var ui = await UiHarness.StartAsync(seed);
        var window = ui.ShowWindow();
        ui.ViewModel.SelectedItems = [ui.ViewModel.AllItems[0]];

        ui.ViewModel.DeleteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(ui.ViewModel.IsDeleteConfirmationOpen);
        Assert.Equal("Delete “keep.zip” from the list?", ui.ViewModel.DeleteConfirmationText);
        Assert.True(window.FindControl<CheckBox>("DeleteFilesBox")!.IsEffectivelyVisible);
        Assert.Single(ui.ViewModel.AllItems); // Nothing deleted yet.

        ui.ViewModel.CancelDeleteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(ui.ViewModel.IsDeleteConfirmationOpen);
        Assert.Single(ui.ViewModel.AllItems);
        Assert.True(File.Exists(file));
    }

    [AvaloniaFact]
    public async Task Confirm_without_the_checkbox_removes_the_rows_but_keeps_the_files()
    {
        var first = Seed("one.zip");
        var second = Seed("two.zip");
        var files = new[] { WriteFile(first), WriteFile(second) };
        await using var ui = await UiHarness.StartAsync(first, second);
        ui.ViewModel.SelectedItems = ui.ViewModel.AllItems.ToList();

        ui.ViewModel.DeleteCommand.Execute(null);
        Assert.Equal("Delete 2 downloads from the list?", ui.ViewModel.DeleteConfirmationText);
        await ui.ViewModel.ConfirmDeleteCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(ui.ViewModel.AllItems);
        Assert.Equal(0, ui.Repository.Count);
        Assert.All(files, f => Assert.True(File.Exists(f)));
    }

    [AvaloniaFact]
    public async Task Confirm_with_the_checkbox_also_deletes_the_file()
    {
        var seed = Seed("gone.zip");
        var file = WriteFile(seed);
        await using var ui = await UiHarness.StartAsync(seed);
        ui.ViewModel.SelectedItems = [ui.ViewModel.AllItems[0]];

        ui.ViewModel.DeleteCommand.Execute(null);
        ui.ViewModel.DeleteAlsoFiles = true;
        await ui.ViewModel.ConfirmDeleteCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(ui.ViewModel.AllItems);
        Assert.False(File.Exists(file));
    }

    [AvaloniaFact]
    public async Task The_checkbox_starts_unchecked_every_time()
    {
        await using var ui = await UiHarness.StartAsync(Seed("a.zip"));
        ui.ViewModel.SelectedItems = [ui.ViewModel.AllItems[0]];

        ui.ViewModel.DeleteCommand.Execute(null);
        ui.ViewModel.DeleteAlsoFiles = true;
        ui.ViewModel.CancelDeleteCommand.Execute(null);
        ui.ViewModel.DeleteCommand.Execute(null);

        Assert.False(ui.ViewModel.DeleteAlsoFiles);
    }
}

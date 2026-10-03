using Avalonia.Headless.XUnit;
using Colibri.Core.Models;

namespace Colibri.App.Tests;

public class DuplicateUrlTests
{
    [AvaloniaFact]
    public async Task Duplicate_is_not_started_before_user_chooses_and_resume_keeps_one_row()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Paused));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");
        await add.DownloadCommand.ExecuteAsync(null);
        Assert.True(add.IsDuplicatePrompt);
        Assert.True(add.CanResumeDuplicate);
        Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken));
        await add.ResumeDuplicateCommand.ExecuteAsync(null);
        // Resumed transfers remain queued until the next engine status poll.
        Assert.Equal(DownloadState.Queued, Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken)).State);
    }

    [AvaloniaFact]
    public async Task Download_again_creates_a_distinct_reserved_file_without_replacing_original()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", DownloadState.Completed));
        var add = ui.ViewModel.CreateAddUrl(LinkContext.Empty, "https://example.com/a.zip");
        await add.DownloadCommand.ExecuteAsync(null);
        Assert.True(add.IsDuplicatePrompt);
        Assert.False(add.CanResumeDuplicate);
        await add.RedownloadDuplicateCommand.ExecuteAsync(null);
        var items = await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.State == DownloadState.Completed && i.FileName == "a.zip");
        // The category folder may differ from an imported v1 row, so check path uniqueness.
        Assert.Equal(2, items.Select(i => Path.Combine(i.SaveFolder, i.FileName)).Distinct().Count());
    }
}

using Colibri.App.ViewModels;

namespace Colibri.App.Services;

/// <summary>
/// Window and clipboard actions the view models need, kept behind an interface so the view models do
/// not create windows themselves.
/// </summary>
public interface IDialogService
{
    /// <summary>Text on the clipboard, or null.</summary>
    Task<string?> ReadClipboardTextAsync();

    Task WriteClipboardTextAsync(string text);

    /// <summary>Shows the Add URL window for <paramref name="viewModel"/>.</summary>
    void ShowAddUrl(AddUrlViewModel viewModel);
    void ShowBulkAdd(BulkAddViewModel viewModel) { }
}

using System.Globalization;
using Avalonia.Threading;
using Colibri.App.Services;
using Colibri.App.ViewModels;
using Colibri.App.Views;
using Colibri.Core.Models;
using Colibri.Core.Platform;
using Colibri.Core.Services;
using Colibri.Core.Settings;
using Colibri.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.App.Tests;

/// <summary>
/// The real view models and download manager over a fake engine and an in-memory repository, for the
/// headless UI tests. Uses en-US so formatted values are predictable.
/// </summary>
internal sealed class UiHarness : IAsyncDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    private UiHarness(DownloadItem[] seed)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

        Repository = new InMemoryDownloadRepository(seed);
        Manager = new DownloadManager(
            [Engine], Repository, new LinkResolverPipeline([new DirectLinkResolver()]), new AppSettings(), Paths,
            NullLogger<DownloadManager>.Instance, TimeProvider.System);
        ViewModel = new MainWindowViewModel(Manager, Shell, Dialogs, NullLogger<MainWindowViewModel>.Instance);
    }

    public TempAppPaths Paths { get; } = new();

    public FakeEngine Engine { get; } = new();

    public InMemoryDownloadRepository Repository { get; }

    public DownloadManager Manager { get; }

    public MainWindowViewModel ViewModel { get; }

    public FakeDialogs Dialogs { get; } = new();

    public FakeShell Shell { get; } = new();

    public MainWindow? Window { get; private set; }

    public static Task<UiHarness> StartAsync(params DownloadItem[] seed) => StartAsync(_ => { }, seed);

    public static async Task<UiHarness> StartAsync(Action<FakeEngine> configureEngine, params DownloadItem[] seed)
    {
        var harness = new UiHarness(seed);
        configureEngine(harness.Engine);
        await harness.ViewModel.InitializeAsync();
        Dispatcher.UIThread.RunJobs();
        return harness;
    }

    public MainWindow ShowWindow()
    {
        Window = new MainWindow { DataContext = ViewModel };
        Window.Show();

        // Showing the window sets a 1 s poll interval; the tests poll by hand instead.
        Manager.SetPollingInterval(Timeout.InfiniteTimeSpan);
        Dispatcher.UIThread.RunJobs();
        return Window;
    }

    /// <summary>One poll tick, then lets the UI thread apply the posted updates.</summary>
    public async Task TickAsync()
    {
        await Manager.PollOnceAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A stored download to seed the repository with.</summary>
    public static DownloadItem Item(string name, DownloadState state, long? total = 1_048_576, long completed = 0, string? handle = null) => new()
    {
        Url = "https://example.com/" + name,
        FileName = name,
        SaveFolder = Path.Combine(Path.GetTempPath(), "colibri-ui-tests"),
        Category = CategoryMapper.FromFileName(name),
        State = state,
        TotalBytes = total,
        CompletedBytes = completed,
        EngineId = "fake",
        EngineHandle = handle,
    };

    public async ValueTask DisposeAsync()
    {
        Window?.Close();
        await Manager.StopAsync();
        Paths.Dispose();
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public string? ClipboardText { get; set; }

    public string? CopiedText { get; private set; }

    public AddUrlViewModel? ShownAddUrl { get; private set; }

    public bool SettingsShown { get; private set; }

    public Task<string?> ReadClipboardTextAsync() => Task.FromResult(ClipboardText);

    public Task WriteClipboardTextAsync(string text)
    {
        CopiedText = text;
        return Task.CompletedTask;
    }

    public void ShowAddUrl(AddUrlViewModel viewModel) => ShownAddUrl = viewModel;

    public void ShowSettings() => SettingsShown = true;
}

internal sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = [];

    public List<string> Revealed { get; } = [];

    public Task OpenFileAsync(string path)
    {
        Opened.Add(path);
        return Task.CompletedTask;
    }

    public Task RevealInFolderAsync(string path)
    {
        Revealed.Add(path);
        return Task.CompletedTask;
    }

    public Task OpenFolderAsync(string path) => Task.CompletedTask;
}

using Avalonia.Headless.XUnit;
using Colibri.App.Services;
using Colibri.Core.Ipc;

namespace Colibri.App.Tests.Services;

public class IpcRequestHandlerTests
{
    private static IpcRequest Parse(string line)
    {
        Assert.True(IpcProtocol.TryParseRequest(line, out var request, out var error), error);
        return request;
    }

    [AvaloniaFact]
    public async Task Add_request_opens_a_prefilled_add_url_window_and_answers_ok()
    {
        await using var ui = await UiHarness.StartAsync();
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => { });
        var request = Parse("""
            {"type":"add","url":"https://example.com/get?id=7","fileName":"setup.exe","size":3145728,
             "referrer":"https://example.com/page","cookies":"sid=abc","userAgent":"Browser/1",
             "mimeType":"application/octet-stream","headers":{"Authorization":"Bearer t","Range":"bytes=0-"}}
            """.ReplaceLineEndings(string.Empty));

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.True(response.Ok);
        var addUrl = Assert.IsType<Colibri.App.ViewModels.AddUrlViewModel>(ui.Dialogs.ShownAddUrl);
        Assert.Equal("https://example.com/get?id=7", addUrl.Url);
        Assert.Equal("setup.exe", addUrl.FileName);
        Assert.Equal("3.0 MB", addUrl.SizeText);
        Assert.EndsWith("Programs", addUrl.SaveFolder);

        // Confirming sends the browser's context along to the engine.
        await addUrl.DownloadCommand.ExecuteAsync(null);
        var call = Assert.Single(ui.Engine.Adds);
        Assert.Equal("https://example.com/page", call.Request.Referrer);
        Assert.Equal("Browser/1", call.Request.UserAgent);
        Assert.Equal("Bearer t", call.Request.Headers["Authorization"]);
        Assert.Equal("sid=abc", call.Request.Headers["Cookie"]);
        Assert.False(call.Request.Headers.ContainsKey("Range"));
    }

    [AvaloniaFact]
    public async Task Activate_shows_the_main_window_unless_minimized_is_asked()
    {
        await using var ui = await UiHarness.StartAsync();
        var shown = 0;
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => shown++);

        Assert.True((await handler.HandleAsync(new ActivateRequest([]), CancellationToken.None)).Ok);
        Assert.Equal(1, shown);

        Assert.True((await handler.HandleAsync(new ActivateRequest(["--minimized"]), CancellationToken.None)).Ok);
        Assert.Equal(1, shown);
    }

    [AvaloniaFact]
    public async Task Add_request_arriving_while_colibri_exits_is_refused_so_the_browser_keeps_its_download()
    {
        await using var ui = await UiHarness.StartAsync();
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => { });
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync(); // The pipe server stops first when Colibri exits.

        var response = await handler.HandleAsync(Parse("""{"type":"add","url":"https://example.com/file.zip"}"""), stopped.Token);

        Assert.False(response.Ok);
        Assert.Null(ui.Dialogs.ShownAddUrl);
    }

    [AvaloniaFact]
    public async Task Activate_from_a_process_that_windows_started_for_a_toast_just_shows_the_window()
    {
        await using var ui = await UiHarness.StartAsync();
        var shown = 0;
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => shown++);

        // The arguments of a COM-activated start (toast clicked) carry no meaning for Colibri.
        var response = await handler.HandleAsync(new ActivateRequest(["-ToastActivated", "-Embedding"]), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal(1, shown);
        Assert.Null(ui.Dialogs.ShownAddUrl);
    }
}

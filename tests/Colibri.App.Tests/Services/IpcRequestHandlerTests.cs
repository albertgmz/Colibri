using Avalonia.Headless.XUnit;
using Colibri.App.Services;
using Colibri.Core.Ipc;

namespace Colibri.App.Tests.Services;

public class IpcRequestHandlerTests
{
    [AvaloniaFact]
    public async Task Cancel_replies_pending_immediately_while_duplicate_rollback_is_in_flight()
    {
        await using var ui = await UiHarness.StartAsync(UiHarness.Item("a.zip", Colibri.Core.Models.DownloadState.Paused));
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => { }, ui.Settings);
        var offer = await handler.HandleAsync(new AddRequest("https://example.com/a.zip", null, Colibri.Core.Models.LinkContext.Empty), CancellationToken.None);
        var add = Assert.IsType<Colibri.App.ViewModels.AddUrlViewModel>(ui.Dialogs.ShownAddUrl);
        await add.DownloadCommand.ExecuteAsync(null);
        var repliedImmediately = false;
        IpcResponse? cancelReply = null;
        ui.Engine.OnResume = _ =>
        {
            var reply = handler.HandleAsync(new CaptureCancelRequest(offer.CaptureId!), CancellationToken.None);
            repliedImmediately = reply.IsCompletedSuccessfully;
            if (repliedImmediately) cancelReply = reply.GetAwaiter().GetResult();
        };
        await add.ResumeDuplicateCommand.ExecuteAsync(null);
        Assert.True(repliedImmediately);
        Assert.Equal("pending", cancelReply!.State);
        var settled = await handler.HandleAsync(new CaptureStatusRequest(offer.CaptureId!), CancellationToken.None);
        Assert.Equal("browser", settled.State);
        var item = Assert.Single(await ui.Manager.GetItemsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Colibri.Core.Engine.EngineDownloadState.Paused,
            (await ui.Engine.GetStatusAsync(item.EngineHandle!, TestContext.Current.CancellationToken))!.State);
    }

    private static IpcRequest Parse(string line)
    {
        Assert.True(IpcProtocol.TryParseRequest(line, out var request, out var error), error);
        return request;
    }

    [AvaloniaFact]
    public async Task Add_request_opens_a_prefilled_add_url_window_and_answers_ok()
    {
        await using var ui = await UiHarness.StartAsync();
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => { }, ui.Settings);
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
    public async Task Ping_is_answered_ok_and_config_returns_the_capture_rules()
    {
        await using var ui = await UiHarness.StartAsync();
        ui.Settings.BrowserCaptureExtensions = ["zip", "iso"];
        ui.Settings.BrowserCaptureMinSizeKiB = 256;
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => { }, ui.Settings);

        Assert.Equal(IpcResponse.Success, await handler.HandleAsync(new PingRequest(), CancellationToken.None));

        var config = await handler.HandleAsync(new ConfigRequest(), CancellationToken.None);
        Assert.True(config.Ok);
        Assert.Equal(["zip", "iso"], config.Config!.Extensions);
        Assert.Equal(256, config.Config.MinSizeKiB);
        Assert.Null(ui.Dialogs.ShownAddUrl);
    }

    [Fact]
    public void Config_keeps_the_capture_rules_within_what_the_host_accepts()
    {
        var settings = new Colibri.Core.Settings.AppSettings
        {
            BrowserCaptureExtensions = ["ZIP", "tar.gz", "", "zip", new string('a', 17), .. Enumerable.Range(0, 300).Select(i => $"e{i}")],
            BrowserCaptureMinSizeKiB = -5,
        };

        var config = IpcRequestHandler.CaptureConfigFrom(settings);

        Assert.Equal("zip", config.Extensions[0]);
        Assert.Equal("e0", config.Extensions[1]);
        Assert.Equal(IpcProtocol.MaxCaptureExtensions, config.Extensions.Count);
        Assert.Equal(0, config.MinSizeKiB);

        // What the host reads back is valid.
        var parsed = IpcProtocol.ParseResponse(IpcProtocol.SerializeResponse(new IpcResponse(true, Config: config)));
        Assert.Equal(config.Extensions, parsed.Config!.Extensions);
    }

    [AvaloniaFact]
    public async Task Activate_shows_the_main_window_unless_minimized_is_asked()
    {
        await using var ui = await UiHarness.StartAsync();
        var shown = 0;
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => shown++, ui.Settings);

        Assert.True((await handler.HandleAsync(new ActivateRequest([]), CancellationToken.None)).Ok);
        Assert.Equal(1, shown);

        Assert.True((await handler.HandleAsync(new ActivateRequest(["--minimized"]), CancellationToken.None)).Ok);
        Assert.Equal(1, shown);
    }

    [AvaloniaFact]
    public async Task Add_request_arriving_while_colibri_exits_is_refused_so_the_browser_keeps_its_download()
    {
        await using var ui = await UiHarness.StartAsync();
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => { }, ui.Settings);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync(); // The pipe server stops first when Colibri exits.

        var response = await handler.HandleAsync(Parse("""{"type":"add","url":"https://example.com/file.zip"}"""), stopped.Token);

        Assert.False(response.Ok);
        Assert.Null(ui.Dialogs.ShownAddUrl);
    }

    [AvaloniaFact]
    public async Task Activate_arriving_while_colibri_exits_is_refused_so_the_new_start_takes_over()
    {
        await using var ui = await UiHarness.StartAsync();
        var shown = 0;
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => shown++, ui.Settings);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync(); // The pipe server stops first when Colibri exits.

        var response = await handler.HandleAsync(new ActivateRequest([]), stopped.Token);

        Assert.False(response.Ok);
        Assert.Equal(0, shown);
    }

    [AvaloniaFact]
    public async Task Activate_from_a_process_that_windows_started_for_a_toast_just_shows_the_window()
    {
        await using var ui = await UiHarness.StartAsync();
        var shown = 0;
        var handler = new IpcRequestHandler(ui.ViewModel, ui.Dialogs, () => shown++, ui.Settings);

        // The arguments of a COM-activated start (toast clicked) carry no meaning for Colibri.
        var response = await handler.HandleAsync(new ActivateRequest(["-ToastActivated", "-Embedding"]), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal(1, shown);
        Assert.Null(ui.Dialogs.ShownAddUrl);
    }
}

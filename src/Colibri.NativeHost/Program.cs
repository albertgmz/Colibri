using Colibri.Platform.Ipc;

namespace Colibri.NativeHost;

/// <summary>
/// The native-messaging host: the browser starts it for each message from the Colibri extension and talks
/// to it over stdin and stdout; it passes the message on to Colibri over the local pipe.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // stdout carries only framed messages to the browser. Anything printed by mistake would break the
        // stream, so the console writers go nowhere; frames are written to the raw stdout stream.
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        var log = HostLog.OpenDefault();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled exception", e.ExceptionObject as Exception);

        try
        {
            // The browser passes the calling extension's origin first (on Windows also --parent-window=...).
            log.Info($"Started by {(args.Length > 0 ? args[0] : "(no origin)")}");
            var client = new ColibriClient(
                IpcPlatform.CreateEndpointProvider().GetEndpoint().PipeName,
                () => AppLauncher.TryStart(AppLauncher.AppExecutablePath(), log),
                IpcPlatform.CreateForegroundHandoff(),
                ColibriTimeouts.Default,
                log);
            var session = new HostSession(Console.OpenStandardInput(), stdout, client.SendAsync, log);
            return session.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Error("The host failed", ex);
            return 1;
        }
    }
}

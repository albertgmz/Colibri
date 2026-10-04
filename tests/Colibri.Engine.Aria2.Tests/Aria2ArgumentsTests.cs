using Colibri.Core.Engine;

namespace Colibri.Engine.Aria2.Tests;

public class Aria2ArgumentsTests
{
    private static readonly EngineOptions Options = new(MaxConcurrentDownloads: 5, ConnectionsPerServer: 8, GlobalSpeedLimitBytesPerSecond: 512_000);

    private static List<string> Build(EngineOptions? options = null) =>
        Aria2Arguments.Build(
            port: 51234,
            configPath: "/data/My Folder/aria2-rpc.conf",
            parentProcessId: 4242,
            logPath: "/data/My Folder/logs/aria2.log",
            options: options ?? Options);

    [Fact]
    public void Rpc_is_enabled_on_the_given_port_and_loopback_only()
    {
        var arguments = Build();

        Assert.Contains("--enable-rpc", arguments);
        Assert.Contains("--rpc-listen-all=false", arguments);
        Assert.Contains("--rpc-listen-port=51234", arguments);
    }

    [Fact]
    public void Secret_is_read_from_the_config_file_and_never_on_the_command_line()
    {
        var arguments = Build();

        Assert.Contains("--conf-path=/data/My Folder/aria2-rpc.conf", arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Aria2_stops_with_the_parent_process()
    {
        Assert.Contains("--stop-with-process=4242", Build());
    }

    [Fact]
    public void Plaintext_session_load_and_save_are_never_enabled()
    {
        var arguments = Build();
        Assert.DoesNotContain(arguments, a => a.StartsWith("--input-file", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.StartsWith("--save-session", StringComparison.Ordinal));
    }

    [Fact]
    public void Paths_with_spaces_stay_in_one_argument()
    {
        Assert.Contains("--conf-path=/data/My Folder/aria2-rpc.conf", Build());
        Assert.DoesNotContain(Build(), value => value.StartsWith("--log=", StringComparison.Ordinal));
    }

    [Fact]
    public void Engine_options_become_global_options()
    {
        var arguments = Build();

        Assert.Contains("--max-concurrent-downloads=5", arguments);
        Assert.Contains("--max-overall-download-limit=512000", arguments);
    }

    [Fact]
    public void Out_of_range_options_are_clamped()
    {
        var arguments = Build(options: new EngineOptions(0, 0, -1));

        Assert.Contains("--max-concurrent-downloads=1", arguments);
        Assert.Contains("--max-overall-download-limit=0", arguments);
    }

    [Fact]
    public void Safe_file_handling_defaults_are_set()
    {
        var arguments = Build();

        Assert.Contains("--continue=true", arguments);
        Assert.Contains("--allow-overwrite=false", arguments);
        Assert.Contains("--auto-file-renaming=false", arguments);
        Assert.Contains("--file-allocation=none", arguments);
    }
}

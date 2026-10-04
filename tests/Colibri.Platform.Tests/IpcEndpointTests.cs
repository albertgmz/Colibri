using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Colibri.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Colibri.Platform.Ipc;

namespace Colibri.Platform.Tests;

public sealed class IpcEndpointTests
{
    // ---- Windows: the SID ----

    [Fact]
    public void Windows_names_come_from_the_sid_so_equal_user_names_do_not_collide()
    {
        var domainBob = WindowsIpcEndpointProvider.FromSid("S-1-5-21-1111111111-2222222222-3333333333-1001", "bob");
        var localBob = WindowsIpcEndpointProvider.FromSid("S-1-5-21-4444444444-5555555555-6666666666-1001", "bob");

        Assert.NotEqual(domainBob.PipeName, localBob.PipeName);
        Assert.NotEqual(domainBob.MutexName, localBob.MutexName);
        Assert.Matches("^colibri-[0-9a-f]{16}$", domainBob.PipeName);
        Assert.Equal(IpcEndpoint.ForUser("S-1-5-21-1111111111-2222222222-3333333333-1001"), domainBob);
    }

    [Fact]
    public void IpcPlatform_picks_the_sid_based_provider_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows identity");

        Assert.IsType<WindowsIpcEndpointProvider>(IpcPlatform.CreateEndpointProvider());
    }

    [Fact]
    public void Windows_without_a_sid_falls_back_to_the_user_name()
    {
        Assert.Equal(IpcEndpoint.ForUser("bob"), WindowsIpcEndpointProvider.FromSid(null, "bob"));
        Assert.Equal(IpcEndpoint.ForUser("bob"), WindowsIpcEndpointProvider.FromSid("", "bob"));
    }

    [Fact]
    [SupportedOSPlatform("windows")] // the skip below guards the Windows-only identity call
    public void Windows_provider_uses_the_current_users_sid()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows identity");

        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(IpcEndpoint.ForUser(identity.User!.Value), new WindowsIpcEndpointProvider().GetEndpoint());
    }

    // ---- Linux: $XDG_RUNTIME_DIR ----

    [Fact]
    public void Linux_puts_the_socket_in_the_runtime_folder()
    {
        var endpoint = LinuxIpcEndpointProvider.Derive("/run/user/1000", "bob", _ => true);

        Assert.Equal(Path.Combine("/run/user/1000", "colibri.sock"), endpoint.PipeName);
        Assert.True(Path.IsPathRooted(endpoint.PipeName)); // .NET then uses it as the socket path itself.
        Assert.Equal(IpcEndpoint.ForUser("bob").MutexName, endpoint.MutexName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("run/user/1000")]
    public void Linux_without_a_usable_runtime_folder_keeps_the_temp_folder_name(string? xdgRuntimeDir)
    {
        Assert.Equal(IpcEndpoint.ForUser("bob"), LinuxIpcEndpointProvider.Derive(xdgRuntimeDir, "bob", _ => true));
    }

    [Fact]
    public void Linux_with_a_missing_runtime_folder_keeps_the_temp_folder_name()
    {
        Assert.Equal(IpcEndpoint.ForUser("bob"), LinuxIpcEndpointProvider.Derive("/run/user/1000", "bob", _ => false));
    }

    [Fact]
    public void Linux_with_a_runtime_folder_too_long_for_a_socket_keeps_the_temp_folder_name()
    {
        var longest = "/" + new string('r', LinuxIpcEndpointProvider.MaxSocketPathBytes - 1 - 1 - "colibri.sock".Length);
        Assert.Equal(LinuxIpcEndpointProvider.MaxSocketPathBytes, Encoding.UTF8.GetByteCount(Path.Combine(longest, "colibri.sock")));
        Assert.NotEqual(IpcEndpoint.ForUser("bob"), LinuxIpcEndpointProvider.Derive(longest, "bob", _ => true));

        Assert.Equal(IpcEndpoint.ForUser("bob"), LinuxIpcEndpointProvider.Derive(longest + "r", "bob", _ => true));
    }

    [Fact]
    public async Task Unix_app_and_host_talk_over_a_socket_at_a_rooted_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain sockets at a path");

        // Short, so the path also fits macOS's limit; the folder plays $XDG_RUNTIME_DIR.
        var folder = Path.Combine(Path.GetTempPath(), "cl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var socket = Path.Combine(folder, "colibri.sock");
        try
        {
            await using var server = new LocalPipeServer(socket, (_, _) => Task.FromResult(IpcResponse.Success), NullLogger.Instance);
            server.Start();

            var response = await LocalPipeClient.SendAsync(socket, new PingRequest(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(response.Ok);
            Assert.True(File.Exists(socket)); // The socket file is exactly there, not in the temp folder.
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- macOS: the per-user temp folder ----

    [Fact]
    public void Mac_socket_path_in_a_typical_tmpdir_fits_the_socket_path_limit()
    {
        // macOS's per-user $TMPDIR looks like this; .NET adds "CoreFxPipe_" and the pipe name. sun_path holds
        // 104 bytes on macOS, the final zero included.
        const string tmpDir = "/var/folders/zz/zyxvpxvq6csfxvn_n0000000000000/T/";
        var socketPath = tmpDir + "CoreFxPipe_" + new UserNameIpcEndpointProvider().GetEndpoint().PipeName;

        Assert.True(Encoding.UTF8.GetByteCount(socketPath) <= 103, socketPath);
    }

    // ---- Windows: handing over the foreground ----

    [Fact]
    [SupportedOSPlatform("windows")] // the skip below guards the Windows-only pipe call
    public async Task Windows_finds_the_process_behind_a_connected_pipe()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows pipes");

        var name = "colibri-test-" + Guid.NewGuid().ToString("N")[..12];
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(5000, TestContext.Current.CancellationToken);
        await accepted;

        Assert.True(WindowsForegroundHandoff.TryGetServerProcessId(client, out var processId));
        Assert.Equal((uint)Environment.ProcessId, processId);
    }
}

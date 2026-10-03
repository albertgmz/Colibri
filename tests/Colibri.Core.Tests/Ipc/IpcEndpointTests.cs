using Colibri.Core.Ipc;

namespace Colibri.Core.Tests.Ipc;

public sealed class IpcEndpointTests
{
    [Fact]
    public void Endpoint_names_are_short_safe_and_differ_per_user()
    {
        var bob = IpcEndpoint.ForUser("bob");

        Assert.Matches("^colibri-[0-9a-f]{16}$", bob.PipeName);
        Assert.Matches("^colibri-instance-[0-9a-f]{16}$", bob.MutexName);
        Assert.Equal(bob, IpcEndpoint.ForUser("bob"));
        Assert.NotEqual(bob.PipeName, IpcEndpoint.ForUser("alice").PipeName);
        Assert.NotEqual(bob.MutexName, IpcEndpoint.ForUser("alice").MutexName);
    }
}

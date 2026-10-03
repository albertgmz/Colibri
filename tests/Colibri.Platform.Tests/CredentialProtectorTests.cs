using Colibri.Core.Platform;
using Colibri.Platform.Security;
using System.Text;

namespace Colibri.Platform.Tests;

public sealed class CredentialProtectorTests
{
    [Fact]
    public void Unix_providers_refuse_to_use_a_different_operating_system()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Throws<CredentialProtectionException>(() => new LinuxCredentialProtector().Protect([1], Guid.NewGuid()));
        if (!OperatingSystem.IsMacOS())
            Assert.Throws<CredentialProtectionException>(() => new MacCredentialProtector().Protect([1], Guid.NewGuid()));
    }

    [Fact]
    public void Unavailable_provider_fails_closed_with_a_payload_free_error()
    {
        var provider = new UnavailableCredentialProtector();
        var bytes = Encoding.UTF8.GetBytes("session=synthetic-secret");
        var id = Guid.NewGuid();
        var error = Assert.Throws<CredentialProtectionException>(() => provider.Protect(bytes, id));
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.Throws<CredentialProtectionException>(() => provider.Unprotect(bytes, id));
    }

    [Fact]
    public void Windows_dpapi_round_trip_authenticates_and_binds_the_record()
    {
        // Unix explicitly exercises the provider's fail-closed behavior, never a plaintext substitute.
        var provider = new WindowsCredentialProtector();
        var id = Guid.NewGuid();
        var plaintext = Encoding.UTF8.GetBytes("Cookie: session=synthetic-secret; unicode=ñ");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<CredentialProtectionException>(() => provider.Protect(plaintext, id));
            return;
        }
        var protectedBytes = provider.Protect(plaintext, id);
        Assert.NotEqual(plaintext, protectedBytes);
        Assert.Equal(plaintext, new WindowsCredentialProtector().Unprotect(protectedBytes, id));
        Assert.Throws<CredentialProtectionException>(() => provider.Unprotect(protectedBytes, Guid.NewGuid()));
        protectedBytes[^1] ^= 1;
        Assert.Throws<CredentialProtectionException>(() => provider.Unprotect(protectedBytes, id));
    }
}

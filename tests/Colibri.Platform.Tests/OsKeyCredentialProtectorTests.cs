using System.Security.Cryptography;
using System.Text;
using Colibri.Core.Platform;
using Colibri.Platform.Security;

namespace Colibri.Platform.Tests;

public sealed class OsKeyCredentialProtectorTests
{
    [Fact]
    public void Protected_payload_survives_new_provider_instance_and_authenticates_the_record()
    {
        var store = new FakeStore();
        var id = Guid.NewGuid();
        var plaintext = Encoding.UTF8.GetBytes("Cookie: synthetic-cookie");
        var ciphertext = new OsKeyCredentialProtector(store).Protect(plaintext, id);
        var reopened = new OsKeyCredentialProtector(store);
        Assert.Equal(plaintext, reopened.Unprotect(ciphertext, id));
        Assert.Throws<CredentialProtectionException>(() => reopened.Unprotect(ciphertext, Guid.NewGuid()));
        ciphertext[^1] ^= 1;
        Assert.Throws<CredentialProtectionException>(() => reopened.Unprotect(ciphertext, id));
    }

    [Fact]
    public void A_missing_or_locked_key_never_falls_back_or_gets_replaced_on_read()
    {
        var store = new FakeStore();
        var id = Guid.NewGuid();
        var provider = new OsKeyCredentialProtector(store);
        var protectedBytes = provider.Protect([1, 2, 3], id);
        store.Unavailable = true;
        Assert.Throws<CredentialProtectionException>(() => provider.Unprotect(protectedBytes, id));
        Assert.False(store.LastCreate);
        Assert.Throws<CredentialProtectionException>(() => provider.Protect([1, 2, 3], id));
    }

    [Fact]
    public void Fresh_nonces_produce_distinct_ciphertexts_and_bad_envelopes_are_refused()
    {
        var provider = new OsKeyCredentialProtector(new FakeStore());
        var id = Guid.NewGuid();
        var first = provider.Protect([1, 2, 3], id);
        var second = provider.Protect([1, 2, 3], id);
        Assert.NotEqual(first, second);
        Assert.Throws<CredentialProtectionException>(() => provider.Unprotect([], id));
        first[0] = 99;
        Assert.Throws<CredentialProtectionException>(() => provider.Unprotect(first, id));
    }

    private sealed class FakeStore : ICredentialKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool Unavailable { get; set; }
        public bool LastCreate { get; private set; }
        public byte[] GetKey(bool createIfMissing)
        {
            LastCreate = createIfMissing;
            if (Unavailable) throw new InvalidOperationException("synthetic-provider-error-with-secret");
            return _key.ToArray();
        }
    }
}

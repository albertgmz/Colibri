using Colibri.Core.Platform;

namespace Colibri.Platform.Security;

/// <summary>Fail-closed placeholder until Keychain/Secret Service support is implemented.</summary>
public sealed class UnavailableCredentialProtector : ICredentialProtector
{
    public byte[] Protect(byte[] plaintext, Guid downloadId) => throw new CredentialProtectionException();
    public byte[] Unprotect(byte[] ciphertext, Guid downloadId) => throw new CredentialProtectionException();
}

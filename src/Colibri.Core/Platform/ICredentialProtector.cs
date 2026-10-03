namespace Colibri.Core.Platform;

/// <summary>Protects persisted request credentials using the current user's OS secret facility.</summary>
/// <remarks>Implementations must authenticate the payload and bind it to the download ID. No plaintext fallback.</remarks>
public interface ICredentialProtector
{
    byte[] Protect(byte[] plaintext, Guid downloadId);
    byte[] Unprotect(byte[] ciphertext, Guid downloadId);
}

/// <summary>Safe to report or log: never contains the secret payload or a provider exception.</summary>
public sealed class CredentialProtectionException : Exception
{
    public CredentialProtectionException() : base("Secure request credential storage is unavailable or could not be unlocked.") { }
}

using System.Security.Cryptography;
using System.Text;
using Colibri.Core.Platform;

namespace Colibri.Platform.Security;

internal interface ICredentialKeyStore
{
    // Reading an existing ciphertext must never create or replace the encryption key.
    byte[] GetKey(bool createIfMissing);
}

/// <summary>AES-GCM with a key held exclusively by the user's OS secret store.</summary>
internal sealed class OsKeyCredentialProtector(ICredentialKeyStore keyStore) : ICredentialProtector
{
    private const int PrefixLength = 29; // version, 12-byte nonce, 16-byte authentication tag
    private static readonly object KeyGate = new();

    public byte[] Protect(byte[] plaintext, Guid downloadId) => Transform(plaintext, downloadId, protect: true);
    public byte[] Unprotect(byte[] ciphertext, Guid downloadId) => Transform(ciphertext, downloadId, protect: false);

    private byte[] Transform(byte[] input, Guid id, bool protect)
    {
        byte[]? key = null;
        byte[]? result = null;
        try
        {
            if (!protect && (input.Length < PrefixLength || input[0] != 1)) throw new CredentialProtectionException();
            lock (KeyGate) key = keyStore.GetKey(createIfMissing: protect);
            if (key.Length != 32) throw new CredentialProtectionException();
            using var aes = new AesGcm(key, 16);
            var binding = Encoding.UTF8.GetBytes($"Colibri/request-headers/v1/{id:D}");
            result = new byte[protect ? input.Length + PrefixLength : input.Length - PrefixLength];
            if (protect)
            {
                result[0] = 1;
                RandomNumberGenerator.Fill(result.AsSpan(1, 12));
                aes.Encrypt(result.AsSpan(1, 12), input, result.AsSpan(PrefixLength), result.AsSpan(13, 16), binding);
            }
            else aes.Decrypt(input.AsSpan(1, 12), input.AsSpan(PrefixLength), input.AsSpan(13, 16), result, binding);
            return result;
        }
        catch (Exception)
        {
            if (result is not null) CryptographicOperations.ZeroMemory(result);
            throw new CredentialProtectionException();
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }
}

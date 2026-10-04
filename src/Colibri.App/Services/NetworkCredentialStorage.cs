using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Colibri.Core.Network;
using Colibri.Core.Platform;

namespace Colibri.App.Services;

internal static class NetworkCredentialStorage
{
    // The sole plaintext representation contains no endpoint, identity or credentials.
    private static readonly byte[] Direct = "Colibri/network/direct/v1"u8.ToArray();
    private sealed record Envelope(int Version, string Purpose, DownloadNetworkPolicy Policy);

    public static byte[] Protect(DownloadNetworkPolicy? policy, Guid id, ICredentialProtector? protector)
    {
        policy ??= new();
        policy.Proxy?.Validate();
        if (policy.Proxy is null && policy.RequiredInterfaceId is null) return Direct.ToArray();
        if (protector is null) throw new CredentialProtectionException();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, "network-policy", policy));
        try { return protector.Protect(plaintext, Binding(id)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static DownloadNetworkPolicy Unprotect(byte[] payload, Guid id, ICredentialProtector? protector)
    {
        if (payload.AsSpan().SequenceEqual(Direct)) return new();
        if (protector is null) throw new CredentialProtectionException();
        byte[] plaintext;
        try { plaintext = protector.Unprotect(payload, Binding(id)); }
        catch (Exception) { throw new CredentialProtectionException(); }
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(plaintext);
            if (envelope is null || envelope.Version != 1 || envelope.Purpose != "network-policy" || envelope.Policy is null)
                throw new CredentialProtectionException();
            envelope.Policy.Proxy?.Validate();
            return envelope.Policy;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { throw new CredentialProtectionException(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static DownloadNetworkPolicy WithoutCredentials(DownloadNetworkPolicy? policy) => new()
    {
        RequiredInterfaceId = policy?.RequiredInterfaceId,
        Proxy = policy?.Proxy is { } proxy ? new DownloadProxy { Endpoint = proxy.Endpoint } : null,
    };

    private static Guid Binding(Guid id) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"Colibri/network-policy/v1/{id:D}")).AsSpan(0, 16));
}

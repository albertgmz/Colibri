namespace Colibri.Core.Network;

/// <summary>A null policy inherits the default; an empty policy explicitly uses the system route.</summary>
public sealed record DownloadNetworkPolicy
{
    public string? RequiredInterfaceId { get; init; }
    public DownloadProxy? Proxy { get; init; }
}

/// <summary>Credentials must be persisted through the credential protector, never settings JSON.</summary>
public sealed class DownloadProxy
{
    public required Uri Endpoint { get; init; }
    public string? UserName { get; init; }
    public string? Password { get; init; }

    public void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri || Endpoint.OriginalString.Any(char.IsControl)
            || Endpoint.Scheme is not ("http" or "https" or "socks5")
            || string.IsNullOrWhiteSpace(Endpoint.Host) || Endpoint.Port is < 1 or > 65535
            || Endpoint.UserInfo.Length != 0 || Endpoint.AbsolutePath != "/"
            || Endpoint.Query.Length != 0 || Endpoint.Fragment.Length != 0
            || InvalidCredential(UserName) || InvalidCredential(Password)
            || (Password is not null && string.IsNullOrEmpty(UserName)))
            throw new ArgumentException("Invalid proxy endpoint or credentials.");
    }

    private static bool InvalidCredential(string? value) =>
        value is { Length: > 4096 } || (value?.Any(char.IsControl) ?? false);

    // Avoid accidental credentials in structured logging and parent record ToString().
    public override string ToString() => "DownloadProxy { credentials redacted }";
}

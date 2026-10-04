namespace Colibri.Core.Network;

public sealed record DownloadNetworkInterface(string Id, string Name, bool IsConnected,
    IReadOnlyList<string> Addresses);

public interface INetworkInterfaceService
{
    /// <summary>True only when all child-process traffic, including DNS and both IP families, is isolated.</summary>
    bool SupportsStrictEnforcement { get; }
    Task<IReadOnlyList<DownloadNetworkInterface>> GetInterfacesAsync(CancellationToken ct);
}

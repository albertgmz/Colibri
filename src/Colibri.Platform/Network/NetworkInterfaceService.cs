using System.Net.NetworkInformation;
using Colibri.Core.Network;

namespace Colibri.Platform.Network;

/// <summary>Inventory only. Socket source binding does not enforce a child-process kill switch.</summary>
public sealed class NetworkInterfaceService : INetworkInterfaceService
{
    public bool SupportsStrictEnforcement => false;

    public Task<IReadOnlyList<DownloadNetworkInterface>> GetInterfacesAsync(CancellationToken ct) =>
        Task.Run<IReadOnlyList<DownloadNetworkInterface>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(adapter => new DownloadNetworkInterface(adapter.Id, adapter.Name,
                    adapter.OperationalStatus == OperationalStatus.Up,
                    adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address.ToString()).ToArray()))
                .OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }, ct);
}

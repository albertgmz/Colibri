using System.Collections.ObjectModel;
using Colibri.App.Resources;
using Colibri.Core.Network;
using Colibri.Core.Services;
using Colibri.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Colibri.App.ViewModels;

public sealed record NetworkAdapterChoice(string? Id, string Name);

public partial class NetworkSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly INetworkInterfaceService _interfaces;
    private readonly DownloadManager _manager;
    private Guid? _downloadId;
    public ObservableCollection<NetworkAdapterChoice> Adapters { get; } = [];
    [ObservableProperty] private NetworkAdapterChoice? _selectedAdapter;
    [ObservableProperty] private string _proxyEndpoint = "";
    [ObservableProperty] private string _proxyUser = "";
    [ObservableProperty] private string _proxyPassword = "";
    [ObservableProperty] private string? _status;

    public NetworkSettingsViewModel(AppSettings settings, ISettingsStore store,
        INetworkInterfaceService interfaces, DownloadManager manager)
    {
        _settings = settings; _store = store; _interfaces = interfaces; _manager = manager;
    }

    public async Task LoadAsync(Guid? downloadId = null)
    {
        _downloadId = downloadId;
        var policy = downloadId is { } id
            ? (await _manager.GetItemsAsync(CancellationToken.None)).FirstOrDefault(i => i.Id == id)?.NetworkPolicy
            : _settings.DefaultNetworkPolicy;
        Adapters.Clear();
        Adapters.Add(new(null, Strings.NetworkSystemRoute));
        foreach (var adapter in await _interfaces.GetInterfacesAsync(CancellationToken.None))
            Adapters.Add(new(adapter.Id, adapter.Name));
        if (policy?.RequiredInterfaceId is { } required && !Adapters.Any(a => a.Id == required))
            Adapters.Add(new(required, required));
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Id == policy?.RequiredInterfaceId) ?? Adapters[0];
        ProxyEndpoint = policy?.Proxy?.Endpoint.AbsoluteUri ?? "";
        ProxyUser = policy?.Proxy?.UserName ?? "";
        ProxyPassword = policy?.Proxy?.Password ?? "";
        Status = null;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        try
        {
            DownloadProxy? proxy = null;
            if (!string.IsNullOrWhiteSpace(ProxyEndpoint))
            {
                proxy = new() { Endpoint = new Uri(ProxyEndpoint.Trim(), UriKind.Absolute),
                    UserName = string.IsNullOrEmpty(ProxyUser) ? null : ProxyUser,
                    Password = string.IsNullOrEmpty(ProxyPassword) ? null : ProxyPassword };
                proxy.Validate();
            }
            else if (ProxyUser.Length != 0 || ProxyPassword.Length != 0) throw new ArgumentException();
            var policy = new DownloadNetworkPolicy { RequiredInterfaceId = SelectedAdapter?.Id, Proxy = proxy };
            if (_downloadId is { } id)
            {
                await _manager.SetNetworkPolicyAsync(id, policy, CancellationToken.None);
                Status = Strings.NetworkDownloadSaved;
            }
            else
            {
                var previous = _settings.DefaultNetworkPolicy;
                _settings.DefaultNetworkPolicy = policy;
                try { await _store.SaveAsync(_settings, CancellationToken.None); }
                catch { _settings.DefaultNetworkPolicy = previous; throw; }
                await _manager.ApplyEngineOptionsAsync(new Colibri.Core.Engine.EngineOptions(
                    _settings.MaxConcurrentDownloads, _settings.ConnectionsPerServer, _settings.GlobalSpeedLimitKiB * 1024L)
                    { NetworkPolicy = policy }, CancellationToken.None);
                Status = policy.RequiredInterfaceId is null ? Strings.NetworkSaved : Strings.NetworkInterfaceLimit;
            }
        }
        catch (Exception)
        {
            // Provider, RPC and endpoint exceptions may contain credentials. Display only safe text.
            Status = SelectedAdapter?.Id is not null ? Strings.NetworkInterfaceLimit : Strings.NetworkInvalid;
        }
    }
}

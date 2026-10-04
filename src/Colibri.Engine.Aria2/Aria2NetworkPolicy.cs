using System.Text.Json.Nodes;
using Colibri.Core.Engine;
using Colibri.Core.Network;

namespace Colibri.Engine.Aria2;

internal static class Aria2NetworkPolicy
{
    public static void Validate(DownloadNetworkPolicy? policy)
    {
        if (policy?.RequiredInterfaceId is not null)
            throw new EngineOperationException("Strict adapter enforcement is unavailable. Colibri cannot isolate DNS, IPv4, IPv6 and child-process traffic to the selected adapter, so this operation was blocked.");
        if (policy?.Proxy is not { } proxy) return;
        try { proxy.Validate(); }
        catch (ArgumentException) { throw new EngineOperationException("Invalid proxy endpoint or credentials."); }
        if (proxy.Endpoint.Scheme != "http")
            throw new EngineOperationException("The aria2 engine supports HTTP proxy endpoints only, including HTTPS downloads through HTTP CONNECT. HTTPS proxy transport and SOCKS proxies are unavailable; this operation was blocked.");
    }

    public static void Apply(JsonObject options, DownloadNetworkPolicy? policy)
    {
        Validate(policy);
        var proxy = policy?.Proxy;
        var endpoint = proxy is null ? "" : new UriBuilder(proxy.Endpoint) { Host = proxy.Endpoint.IdnHost }.Uri.GetLeftPart(UriPartial.Authority);
        // Explicitly replace every protocol option: environment/global proxy settings must not
        // bypass the selected proxy or the explicit system-route override.
        foreach (var protocol in new[] { "all", "http", "https", "ftp" })
        {
            options[$"{protocol}-proxy"] = endpoint;
            options[$"{protocol}-proxy-user"] = proxy?.UserName ?? "";
            options[$"{protocol}-proxy-passwd"] = proxy?.Password ?? "";
        }
        options["no-proxy"] = "";
        options["proxy-method"] = "tunnel";
        // A downloaded .torrent/.metalink must never create ungoverned network work.
        options["follow-torrent"] = "false";
        options["follow-metalink"] = "false";
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Colibri.Core.Models;

namespace Colibri.Engine.Aria2;

/// <summary>
/// Builds what aria2.addUri needs for one download: the URL and the per-download options.
/// </summary>
internal static class Aria2AddOptions
{
    /// <summary>aria2 refuses more than 16 connections per server.</summary>
    public const int MaxConnectionsPerServer = 16;

    /// <summary>
    /// A new random GID: 16 lower-case hex characters. Colibri chooses the GID itself (aria2 accepts it
    /// through the "gid" option), so the handle is known before aria2 answers and survives restarts
    /// through the session file.
    /// </summary>
    public static string NewGid() => RandomNumberGenerator.GetHexString(16, lowercase: true);

    /// <summary>
    /// The URL to send to aria2. aria2 cannot resolve Unicode host names, so the host is sent in its
    /// punycode form (bücher.example -> xn--bcher-kva.example).
    /// </summary>
    public static string ToAria2Url(Uri uri)
    {
        if (uri.IdnHost == uri.Host)
        {
            return uri.AbsoluteUri;
        }

        return new UriBuilder(uri) { Host = uri.IdnHost }.Uri.AbsoluteUri;
    }

    /// <summary>
    /// The aria2 options for one download. All values are strings, as aria2 expects. Headers whose
    /// name or value is not valid HTTP are left out (they would allow header injection).
    /// </summary>
    public static JsonObject Build(DownloadRequest request, string saveFolder, string fileName, string gid, int connectionsPerServer)
    {
        var connections = Math.Clamp(connectionsPerServer, 1, MaxConnectionsPerServer)
            .ToString(CultureInfo.InvariantCulture);

        var options = new JsonObject
        {
            ["gid"] = gid,
            ["dir"] = saveFolder,
            ["out"] = fileName,
            ["max-connection-per-server"] = connections,

            // As many pieces as connections, so every allowed connection has something to fetch.
            ["split"] = connections,
        };

        var headers = new JsonArray();
        foreach (var (name, value) in request.Headers)
        {
            // The dedicated fields below win over the same header in the dictionary.
            var replaced = (request.Cookies is not null && name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                || (request.Referrer is not null && name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                || (request.UserAgent is not null && name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase));

            if (!replaced && HttpHeaders.IsValidName(name) && HttpHeaders.IsValidValue(value))
            {
                headers.Add($"{name}: {value}");
            }
        }

        if (HttpHeaders.ValidValueOrNull(request.Cookies) is { } cookies)
        {
            headers.Add($"Cookie: {cookies}");
        }

        if (headers.Count > 0)
        {
            options["header"] = headers;
        }

        if (HttpHeaders.ValidValueOrNull(request.Referrer) is { } referrer)
        {
            options["referer"] = referrer;
        }

        if (HttpHeaders.ValidValueOrNull(request.UserAgent) is { } userAgent)
        {
            options["user-agent"] = userAgent;
        }

        return options;
    }
}

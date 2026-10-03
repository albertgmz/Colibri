using System.Text.Json;
using Colibri.Core.Platform;

namespace Colibri.Platform.BrowserHost;

/// <summary>
/// The native-messaging host manifest Chrome and Edge read:
/// <c>{"name", "description", "path", "type": "stdio", "allowed_origins": [...]}</c>. <c>path</c> is
/// absolute; each origin is <c>chrome-extension://&lt;id&gt;/</c> with the trailing slash and no wildcards.
/// </summary>
internal static class NativeHostManifest
{
    public static string FileName(NativeHostRegistration registration) => registration.HostName + ".json";

    /// <summary>A registration pointing at a missing host would look installed while every capture fails.</summary>
    /// <exception cref="FileNotFoundException">The host executable does not exist.</exception>
    public static void EnsureHostExists(NativeHostRegistration registration)
    {
        if (!File.Exists(registration.HostExecutablePath))
        {
            throw new FileNotFoundException("The native-messaging host was not found.", registration.HostExecutablePath);
        }
    }

    public static string Build(NativeHostRegistration registration, bool firefox = false)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("name", registration.HostName);
            json.WriteString("description", registration.Description);
            json.WriteString("path", registration.HostExecutablePath);
            json.WriteString("type", "stdio");
            json.WriteStartArray(firefox ? "allowed_extensions" : "allowed_origins");
            foreach (var origin in firefox ? new[] { Colibri.Core.Ipc.BrowserProtocol.FirefoxId } : registration.AllowedOrigins)
            {
                json.WriteStringValue(origin);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    /// True when <paramref name="json"/> registers the same host name, host path (compared with
    /// <paramref name="pathComparer"/>: Windows paths ignore case) and set of origins as <paramref name="expected"/>.
    /// The description does not count. Unreadable JSON does not match.
    /// </summary>
    public static bool Matches(string json, NativeHostRegistration expected, StringComparer pathComparer, bool firefox = false)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || StringProperty(root, "name") != expected.HostName
                || StringProperty(root, "type") != "stdio"
                || !pathComparer.Equals(StringProperty(root, "path") ?? string.Empty, expected.HostExecutablePath)
                || !root.TryGetProperty(firefox ? "allowed_extensions" : "allowed_origins", out var origins)
                || origins.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var found = origins.EnumerateArray().Select(o => o.ValueKind == JsonValueKind.String ? o.GetString() : null).ToHashSet();
            return found.SetEquals(firefox ? new[] { Colibri.Core.Ipc.BrowserProtocol.FirefoxId } : expected.AllowedOrigins);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

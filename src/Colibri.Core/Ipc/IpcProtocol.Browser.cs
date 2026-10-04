using System.Text.Json;

namespace Colibri.Core.Ipc;

public static partial class IpcProtocol
{
    private static bool TryParseBrowserV2(JsonElement root, string type, out IpcRequest? request, out string? error)
    {
        request = null;
        error = null;
        if (root.TryGetProperty("protocolVersion", out var version) &&
            (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != BrowserProtocol.Version))
            throw new FieldException("Unsupported protocol version.");
        switch (type)
        {
            case "hello":
                if (!root.TryGetProperty("protocolVersion", out version) || version.GetInt32() != BrowserProtocol.Version)
                    throw new FieldException("Unsupported protocol version.");
                request = new HelloRequest(BrowserProtocol.Version, OptionalString(root, "extensionVersion", 128) ?? "unknown",
                    ReadStringArray(root, "capabilities", 32, 64));
                break;
            case "open": request = new OpenRequest(); break;
            case "capture-status": request = new CaptureStatusRequest(ReadCaptureId(root)); break;
            case "capture-cancel": request = new CaptureCancelRequest(ReadCaptureId(root)); break;
            case "bulk-add":
                if (!root.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array || links.GetArrayLength() is < 1 or > 500)
                    throw new FieldException("'links' must contain between 1 and 500 links.");
                var parsed = new List<AddRequest>();
                foreach (var link in links.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Object) throw new FieldException("Each link must be an object.");
                    if (!TryParseAdd(link, out var add, out error)) return false;
                    parsed.Add((AddRequest)add!);
                }
                request = new BulkAddRequest(parsed);
                break;
            case "settings-update":
                if (!root.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.Object)
                    throw new FieldException("'patch' must be an object.");
                var modifier = OptionalString(patch, "bypassModifier", 16);
                if (modifier is not (null or "none" or "alt" or "shift" or "ctrl"))
                    throw new FieldException("Unsupported bypass modifier.");
                var sites = patch.TryGetProperty("excludedSites", out _)
                    ? ReadStringArray(patch, "excludedSites", 256, 253) : null;
                if (sites?.Any(s => Uri.CheckHostName(s) == UriHostNameType.Unknown) == true)
                    throw new FieldException("Excluded sites must be host names.");
                request = new SettingsUpdateRequest(new CaptureSettingsPatch(OptionalBool(patch, "enabled"), sites,
                    OptionalBool(patch, "capturePrivate"), modifier));
                break;
        }
        return request is not null;
    }

    private static string ReadCaptureId(JsonElement root)
    {
        var id = OptionalString(root, "captureId", 64);
        if (!Guid.TryParseExact(id, "D", out _)) throw new FieldException("Invalid capture id.");
        return id!;
    }

    private static bool? OptionalBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FieldException($"'{name}' must be a boolean.");
        return value.GetBoolean();
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name, int maxCount, int maxLength)
    {
        if (!root.TryGetProperty(name, out var values)) return [];
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > maxCount)
            throw new FieldException($"'{name}' must be a bounded string array.");
        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length is 0 || value.GetString()!.Length > maxLength)
                throw new FieldException($"'{name}' contains an invalid string.");
            result.Add(value.GetString()!);
        }
        return result;
    }

    private static IReadOnlyList<string> ReadRedirects(JsonElement root)
    {
        var redirects = ReadStringArray(root, "redirects", 32, Services.UrlPolicy.MaxLength);
        if (redirects.Any(url => !Services.UrlPolicy.TryValidate(url, out _, out string? _)))
            throw new FieldException("Invalid redirect URL.");
        return redirects;
    }

    private static int? ReadResponseStatus(JsonElement root)
    {
        if (!root.TryGetProperty("responseStatus", out var status)) return null;
        if (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var number) || number is < 100 or > 599)
            throw new FieldException("Invalid response status.");
        return number;
    }

    private static string ReadRequestMethod(JsonElement root)
    {
        var method = OptionalString(root, "requestMethod", 16) ?? "GET";
        if (method != "GET") throw new FieldException("Only GET downloads can be replayed.");
        return method;
    }

    private static void WriteBrowserV2Request(Utf8JsonWriter json, IpcRequest request)
    {
        json.WriteNumber("protocolVersion", BrowserProtocol.Version);
        switch (request)
        {
            case HelloRequest hello:
                json.WriteString("type", "hello"); json.WriteString("extensionVersion", hello.ExtensionVersion);
                WriteArray(json, "capabilities", hello.Capabilities); break;
            case OpenRequest: json.WriteString("type", "open"); break;
            case CaptureStatusRequest status:
                json.WriteString("type", "capture-status"); json.WriteString("captureId", status.CaptureId); break;
            case CaptureCancelRequest cancel:
                json.WriteString("type", "capture-cancel"); json.WriteString("captureId", cancel.CaptureId); break;
            case BulkAddRequest bulk:
                json.WriteString("type", "bulk-add"); json.WriteStartArray("links");
                foreach (var link in bulk.Links) json.WriteRawValue(SerializeRequest(link));
                json.WriteEndArray(); break;
            case SettingsUpdateRequest update:
                json.WriteString("type", "settings-update"); json.WriteStartObject("patch");
                if (update.Patch.Enabled is { } enabled) json.WriteBoolean("enabled", enabled);
                if (update.Patch.CapturePrivate is { } capturePrivate) json.WriteBoolean("capturePrivate", capturePrivate);
                if (update.Patch.ExcludedSites is { } sites) WriteArray(json, "excludedSites", sites);
                WriteOptional(json, "bypassModifier", update.Patch.BypassModifier);
                json.WriteEndObject(); break;
            default: throw new ArgumentException("Unknown request type.", nameof(request));
        }
    }

    private static void WriteBrowserV2Response(Utf8JsonWriter json, IpcResponse response)
    {
        WriteOptional(json, "state", response.State); WriteOptional(json, "captureId", response.CaptureId);
        if (response.State is { } state)
        {
            json.WriteBoolean("pending", state == "pending"); json.WriteBoolean("accepted", state == "accepted");
        }
        if (response.ProtocolVersion is { } version) json.WriteNumber("protocolVersion", version);
        WriteOptional(json, "appVersion", response.AppVersion);
        if (response.Capabilities is { } capabilities) WriteArray(json, "capabilities", capabilities);
        if (response.Config is { } config)
        {
            json.WriteBoolean("enabled", config.Enabled); WriteArray(json, "excludedSites", config.ExcludedSites ?? []);
            json.WriteBoolean("capturePrivate", config.CapturePrivate); json.WriteString("bypassModifier", config.BypassModifier);
            json.WriteString("theme", config.Theme); json.WriteString("accent", config.Accent);
            json.WriteString("palette", Settings.BackgroundPalettes.Normalize(config.Palette));
        }
    }

    private static void WriteArray(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values) json.WriteStringValue(value);
        json.WriteEndArray();
    }
}

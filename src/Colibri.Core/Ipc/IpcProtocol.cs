using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Colibri.Core.Models;
using Colibri.Core.Services;

namespace Colibri.Core.Ipc;

/// <summary>
/// The local pipe protocol between Colibri processes (a second app instance, and the browser's
/// native-messaging host) and the running app.
/// </summary>
/// <remarks>
/// One request and one response per connection. Each message is one UTF-8 JSON object on one line.
/// Requests:
/// <code>
/// {"type":"activate","args":["--minimized"]}
/// {"type":"add","url":"https://...","finalUrl":"...","fileName":"...","referrer":"...","cookies":"...",
///  "userAgent":"...","size":123,"mimeType":"...","headers":{"Name":"value"}}
/// {"type":"ping"}
/// {"type":"config"}
/// </code>
/// Response: <c>{"ok":true}</c> or <c>{"ok":false,"error":"..."}</c>; the answer to "config" also has
/// <c>"captureExtensions":["zip",...],"minSizeKiB":0</c>.
/// Unknown request types and fields of the wrong type are rejected; unknown fields are ignored. The
/// "add" payload comes from a web page through the browser and is treated as untrusted.
/// </remarks>
public static partial class IpcProtocol
{
    /// <summary>Longest message accepted, in bytes (newline excluded).</summary>
    public const int MaxLineBytes = 1024 * 1024;

    public const int MaxArgs = 64;
    public const int MaxArgLength = 4096;
    public const int MaxFileNameLength = 1024;
    public const int MaxCookiesLength = 64 * 1024;
    public const int MaxUserAgentLength = 1024;
    public const int MaxMimeTypeLength = 255;
    public const int MaxHeaders = 64;
    public const int MaxHeaderNameLength = 256;
    public const int MaxHeaderValueLength = 8192;
    public const int MaxCaptureExtensions = 256;
    public const int MaxCaptureExtensionLength = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Parses and validates one request line. Returns false with an English <paramref name="error"/> when
    /// the line is not valid JSON, has the wrong shape, an unknown type, or values that break the limits.
    /// </summary>
    public static bool TryParseRequest(string line, [NotNullWhen(true)] out IpcRequest? request, [NotNullWhen(false)] out string? error)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The request must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                error = "The request has no type.";
                return false;
            }

            if (root.TryGetProperty("protocolVersion", out var protocolVersion) &&
                (protocolVersion.ValueKind != JsonValueKind.Number || !protocolVersion.TryGetInt32(out var versionNumber) || versionNumber != BrowserProtocol.Version))
                throw new FieldException("Unsupported protocol version.");

            switch (type.GetString())
            {
                case "hello":
                case "open":
                case "capture-status":
                case "capture-cancel":
                case "bulk-add":
                case "settings-update":
                    return TryParseBrowserV2(root, type.GetString()!, out request, out error);
                case "activate":
                    return TryParseActivate(root, out request, out error);
                case "add":
                    return TryParseAdd(root, out request, out error);
                case "ping":
                    request = new PingRequest();
                    error = null;
                    return true;
                case "config":
                    request = new ConfigRequest();
                    error = null;
                    return true;
                default:
                    error = "Unknown request type.";
                    return false;
            }
        }
        catch (JsonException)
        {
            error = "The request is not valid JSON.";
            return false;
        }
        catch (FieldException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Writes a request as one JSON line (without the newline).</summary>
    public static string SerializeRequest(IpcRequest request)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            switch (request)
            {
                case ActivateRequest activate:
                    json.WriteString("type", "activate");
                    json.WriteStartArray("args");
                    foreach (var arg in activate.Args)
                    {
                        json.WriteStringValue(arg);
                    }

                    json.WriteEndArray();
                    break;

                case AddRequest add:
                    var context = add.Context;
                    json.WriteString("type", "add");
                    json.WriteString("url", add.Url);
                    WriteOptional(json, "finalUrl", add.FinalUrl);
                    WriteOptional(json, "fileName", context.FileName);
                    WriteOptional(json, "referrer", context.Referrer);
                    WriteOptional(json, "cookies", context.Cookies);
                    WriteOptional(json, "userAgent", context.UserAgent);
                    if (context.Size is { } size)
                    {
                        json.WriteNumber("size", size);
                    }

                    WriteOptional(json, "mimeType", context.MimeType);
                    WriteArray(json, "redirects", context.Redirects);
                    WriteOptional(json, "contentDisposition", context.ContentDisposition);
                    if (context.ResponseStatus is { } responseStatus) json.WriteNumber("responseStatus", responseStatus);
                    json.WriteString("requestMethod", context.RequestMethod);
                    json.WriteStartObject("headers");
                    foreach (var (name, value) in context.Headers)
                    {
                        json.WriteString(name, value);
                    }

                    json.WriteEndObject();
                    break;

                case PingRequest:
                    json.WriteString("type", "ping");
                    break;

                case ConfigRequest:
                    json.WriteString("type", "config");
                    break;

                default:
                    WriteBrowserV2Request(json, request);
                    break;
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes a response as one JSON line (without the newline).</summary>
    public static string SerializeResponse(IpcResponse response)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", response.Ok);
            WriteOptional(json, "error", response.Error);
            WriteBrowserV2Response(json, response);
            if (response.Config is { } config)
            {
                json.WriteStartArray("captureExtensions");
                foreach (var extension in config.Extensions)
                {
                    json.WriteStringValue(extension);
                }

                json.WriteEndArray();
                json.WriteNumber("minSizeKiB", config.MinSizeKiB);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Parses a response line. Capture rules, when present, must be well formed.</summary>
    /// <exception cref="InvalidDataException">The line is not a valid response.</exception>
    public static IpcResponse ParseResponse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("The response has no 'ok' field.");
            }

            var error = root.TryGetProperty("error", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            return new IpcResponse(ok.GetBoolean(), error, ReadCaptureConfig(root),
                OptionalString(root, "state", 32), OptionalString(root, "captureId", 64),
                root.TryGetProperty("protocolVersion", out var version) ? version.GetInt32() : null,
                OptionalString(root, "appVersion", 128), root.TryGetProperty("capabilities", out _) ? ReadStringArray(root, "capabilities", 32, 64) : null);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The response is not valid JSON.", ex);
        }
        catch (Exception ex) when (ex is FieldException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The response contains invalid fields.", ex);
        }
    }

    /// <summary>
    /// Reads one line of at most <paramref name="maxBytes"/> bytes. Returns the line without its newline
    /// (a missing newline at the end of the stream is accepted), null when the stream ended before any
    /// data, and <c>TooLong</c> = true as soon as the limit is passed (the rest is not read).
    /// </summary>
    /// <exception cref="InvalidDataException">The line is not valid UTF-8.</exception>
    public static async Task<(string? Line, bool TooLong)> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var data = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return data.Length == 0 ? (null, false) : (Decode(data.GetBuffer(), (int)data.Length), false);
            }

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            var take = newline >= 0 ? newline : read;
            if (data.Length + take > maxBytes)
            {
                return (null, true);
            }

            data.Write(chunk, 0, take);
            if (newline >= 0)
            {
                return (Decode(data.GetBuffer(), (int)data.Length), false);
            }
        }
    }

    /// <summary>Writes <paramref name="line"/> and a newline, then flushes.</summary>
    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await stream.FlushAsync(ct);
    }

    private static string Decode(byte[] bytes, int length)
    {
        // Tolerates a "\r\n" line ending.
        if (length > 0 && bytes[length - 1] == '\r')
        {
            length--;
        }

        try
        {
            return StrictUtf8.GetString(bytes, 0, length);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The message is not valid UTF-8.", ex);
        }
    }

    /// <summary>Reads "captureExtensions" and "minSizeKiB"; null when neither is present.</summary>
    private static CaptureConfig? ReadCaptureConfig(JsonElement root)
    {
        var hasExtensions = root.TryGetProperty("captureExtensions", out var array);
        var hasMinSize = root.TryGetProperty("minSizeKiB", out var minSize);
        if (!hasExtensions && !hasMinSize)
        {
            return null;
        }

        if (!hasExtensions || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxCaptureExtensions
            || !hasMinSize || minSize.ValueKind != JsonValueKind.Number || !minSize.TryGetInt32(out var minSizeKiB) || minSizeKiB < 0)
        {
            throw new InvalidDataException("The capture rules in the response are not valid.");
        }

        var extensions = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var extension = item.ValueKind == JsonValueKind.String ? item.GetString()! : string.Empty;
            if (extension.Length is 0 or > MaxCaptureExtensionLength || !extension.All(char.IsAsciiLetterOrDigit))
            {
                throw new InvalidDataException("The capture rules in the response are not valid.");
            }

            extensions.Add(extension.ToLowerInvariant());
        }

        return new CaptureConfig(extensions, minSizeKiB,
            OptionalBool(root, "enabled") ?? true, ReadStringArray(root, "excludedSites", 256, 253),
            OptionalBool(root, "capturePrivate") ?? false, OptionalString(root, "bypassModifier", 16) ?? "none",
            OptionalString(root, "theme", 16) ?? "system", OptionalString(root, "accent", 32) ?? "#C42B1C",
            Settings.BackgroundPalettes.Normalize(root.TryGetProperty("palette", out var palette) && palette.ValueKind == JsonValueKind.String ? palette.GetString() : null));
    }

    private static bool TryParseActivate(JsonElement root, out IpcRequest? request, out string? error)
    {
        var args = new List<string>();
        if (root.TryGetProperty("args", out var array) && array.ValueKind != JsonValueKind.Null)
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                throw new FieldException("'args' must be an array of strings.");
            }

            if (array.GetArrayLength() > MaxArgs)
            {
                throw new FieldException("'args' has too many items.");
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new FieldException("'args' must be an array of strings.");
                }

                var arg = item.GetString()!;
                if (arg.Length > MaxArgLength)
                {
                    throw new FieldException("An argument is too long.");
                }

                args.Add(arg);
            }
        }

        request = new ActivateRequest(args);
        error = null;
        return true;
    }

    private static bool TryParseAdd(JsonElement root, out IpcRequest? request, out string? error)
    {
        request = null;
        var url = OptionalString(root, "url", UrlPolicy.MaxLength);
        if (!UrlPolicy.TryValidate(url, out var uri, out string? urlError))
        {
            error = "'url' is not accepted: " + urlError;
            return false;
        }

        var finalUrl = OptionalString(root, "finalUrl", UrlPolicy.MaxLength);
        Uri? finalUri = null;
        if (finalUrl is not null && !UrlPolicy.TryValidate(finalUrl, out finalUri, out urlError))
        {
            error = "'finalUrl' is not accepted: " + urlError;
            return false;
        }

        long? size = null;
        if (root.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind != JsonValueKind.Null)
        {
            if (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out var value) || value < 0)
            {
                error = "'size' must be a whole number of bytes, 0 or more.";
                return false;
            }

            size = value;
        }

        var context = new LinkContext
        {
            FileName = OptionalString(root, "fileName", MaxFileNameLength),
            Referrer = OptionalHeaderValue(root, "referrer", UrlPolicy.MaxLength),
            Cookies = OptionalHeaderValue(root, "cookies", MaxCookiesLength),
            UserAgent = OptionalHeaderValue(root, "userAgent", MaxUserAgentLength),
            MimeType = OptionalHeaderValue(root, "mimeType", MaxMimeTypeLength),
            Size = size,
            Headers = ReadHeaders(root),
            Redirects = ReadRedirects(root),
            ContentDisposition = OptionalHeaderValue(root, "contentDisposition", 8192),
            ResponseStatus = ReadResponseStatus(root),
            RequestMethod = ReadRequestMethod(root),
        };

        request = new AddRequest(uri.AbsoluteUri, finalUri?.AbsoluteUri, context);
        error = null;
        return true;
    }

    /// <summary>
    /// Reads the "headers" object. Names and values must be valid HTTP; headers the engine must not get
    /// (see <see cref="HttpHeaders.IsForwardable"/>) are dropped quietly, since browsers report them routinely.
    /// </summary>
    private static Dictionary<string, string> ReadHeaders(JsonElement root)
    {
        var headers = HttpHeaders.Create();
        if (!root.TryGetProperty("headers", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return headers;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FieldException("'headers' must be an object of names and string values.");
        }

        var count = 0;
        foreach (var header in element.EnumerateObject())
        {
            if (++count > MaxHeaders)
            {
                throw new FieldException("'headers' has too many entries.");
            }

            if (header.Value.ValueKind != JsonValueKind.String)
            {
                throw new FieldException("'headers' must be an object of names and string values.");
            }

            var value = header.Value.GetString()!;
            if (header.Name.Length > MaxHeaderNameLength || !HttpHeaders.IsValidName(header.Name)
                || value.Length > MaxHeaderValueLength || !HttpHeaders.IsValidValue(value))
            {
                throw new FieldException("A header name or value is not valid.");
            }

            if (HttpHeaders.IsForwardable(header.Name))
            {
                headers[header.Name] = value;
            }
        }

        return headers;
    }

    /// <summary>A string field, or null when missing or JSON null. Throws when it has another type or is too long.</summary>
    private static string? OptionalString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new FieldException($"'{name}' must be a string.");
        }

        var value = element.GetString()!;
        if (value.Length > maxLength)
        {
            throw new FieldException($"'{name}' is too long.");
        }

        return value.Length == 0 ? null : value;
    }

    /// <summary>Like <see cref="OptionalString"/>, and the value must be safe to send in a header.</summary>
    private static string? OptionalHeaderValue(JsonElement root, string name, int maxLength)
    {
        var value = OptionalString(root, name, maxLength);
        if (value is not null && !HttpHeaders.IsValidValue(value))
        {
            throw new FieldException($"'{name}' contains characters that are not allowed.");
        }

        return value;
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
        {
            json.WriteString(name, value);
        }
    }

    /// <summary>A field has the wrong type or breaks a limit; the message goes into the error response.</summary>
    private sealed class FieldException(string message) : Exception(message);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Colibri.Core.Settings;

public static class BackgroundPalettes
{
    public static IReadOnlyList<string> Ids { get; } = ["warm", "graphite", "ocean", "forest"];

    public static string Normalize(string? value)
    {
        var id = value?.Trim().ToLowerInvariant();
        return id is "warm" or "graphite" or "ocean" or "forest" ? id : "warm";
    }
}

/// <summary>A malformed palette preference must not discard unrelated saved settings.</summary>
public sealed class BackgroundPaletteJsonConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return BackgroundPalettes.Normalize(reader.GetString());
        using var ignored = JsonDocument.ParseValue(ref reader);
        return "warm";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(BackgroundPalettes.Normalize(value));
}

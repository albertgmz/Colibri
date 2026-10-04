using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Colibri.Core.Settings;

public static class LanguagePreference
{
    private static readonly IReadOnlyDictionary<string, string> Cultures = CultureInfo.GetCultures(CultureTypes.AllCultures)
        .Where(culture => culture.Name.Length > 0).ToDictionary(culture => culture.Name, culture => culture.Name, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? value)
    {
        var id = value?.Trim();
        if (string.IsNullOrEmpty(id) || id.Equals("system", StringComparison.OrdinalIgnoreCase)) return "system";
        return Cultures.TryGetValue(id, out var name) ? name : "system";
    }
}

public sealed class LanguagePreferenceJsonConverter : JsonConverter<string>
{
    public override bool HandleNull => true;
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return LanguagePreference.Normalize(reader.GetString());
        using var ignored = JsonDocument.ParseValue(ref reader);
        return "system";
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(LanguagePreference.Normalize(value));
}

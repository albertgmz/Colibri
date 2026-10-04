using Colibri.Core.Settings;

namespace Colibri.Core.Services;

public sealed record BrowserCapturePolicy(Dictionary<string, string> Categories, Dictionary<string, string> Extensions);
public sealed record CaptureDecision(string Action, string Reason, string Category);

/// <summary>Bounded rules use host/path only. Never retain the evaluated URL.</summary>
public static class BrowserCaptureRules
{
    public static BrowserCapturePolicy Normalize(BrowserCapturePolicy policy) => new(
        CaptureCatalog.Categories.ToDictionary(c => c.Id, c => IsAction(policy.Categories?.GetValueOrDefault(c.Id)) ? policy.Categories![c.Id] : "browser"),
        (policy.Extensions ?? new()).Where(p => p.Key.Length is > 0 and <= 16 && p.Key.All(char.IsAsciiLetterOrDigit))
            .Take(256).GroupBy(p => p.Key.ToLowerInvariant()).ToDictionary(g => g.Key, g => IsAction(g.Last().Value) ? g.Last().Value : "browser"));
    public static bool IsAction(string? action) => action is "capture" or "ask" or "browser";
    public static string BaseName(string? name) => (name ?? "").Replace('\\', '/').Split('/').Last();
    public static string Extension(string? name)
    {
        var basename = BaseName(name);
        var dot = basename.LastIndexOf('.');
        return dot > 0 && dot < basename.Length - 1 ? basename[(dot + 1)..].ToLowerInvariant() : "";
    }
    public static string Category(string? name) => CaptureCatalog.Categories
        .First(c => c.Category == CategoryMapper.FromFileName(BaseName(name))).Id;
    public static BrowserCapturePolicy Migrate(IEnumerable<string> legacy)
    {
        var extensions = legacy.Where(e => e is not null && e.Length is > 0 and <= 16 && e.All(char.IsAsciiLetterOrDigit)).Select(e => e.ToLowerInvariant()).Distinct().Take(256).ToHashSet();
        return new(CaptureCatalog.Categories.ToDictionary(c => c.Id, _ => "ask"),
            CaptureCatalog.Categories.SelectMany(c => c.Extensions).Concat(extensions).Distinct()
                .ToDictionary(e => e, e => extensions.Contains(e) ? "ask" : "browser"));
    }
    public static bool TryNormalizeRule(string text, out string rule)
    {
        rule = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.Length is < 3 or > 512 || text.Any(char.IsControl) || text.Contains('?') || text.Contains('#')) return false;
        var colon = text.IndexOf(':');
        if (colon < 0) return false;
        var kind = text[..colon].ToLowerInvariant();
        var value = text[(colon + 1)..];
        if (kind == "type")
        {
            value = value.TrimStart('.').ToLowerInvariant();
            if (value.Length is < 1 or > 16 || !value.All(char.IsAsciiLetterOrDigit)) return false;
        }
        else if (kind is "host" or "domain" or "path")
        {
            var slash = value.IndexOf('/');
            var host = slash < 0 ? value : value[..slash];
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown
                || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) return false;
            if (kind == "path" && (slash < 0 || value[slash..].Contains('*'))) return false;
            if (kind != "path" && slash >= 0) return false;
            value = host.ToLowerInvariant() + (slash < 0 ? "" : value[slash..]);
        }
        else return false;
        rule = kind + ":" + value;
        return true;
    }
    public static bool Matches(string rule, string url, string? fileName)
    {
        if (!TryNormalizeRule(rule, out var normalized) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var split = normalized.IndexOf(':'); var kind = normalized[..split]; var value = normalized[(split + 1)..];
        return kind switch
        {
            "type" => Extension(fileName) == value,
            "host" => uri.IdnHost.Equals(value, StringComparison.OrdinalIgnoreCase),
            "domain" => uri.IdnHost.Equals(value, StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith("." + value, StringComparison.OrdinalIgnoreCase),
            "path" => uri.IdnHost.Equals(value.Split('/')[0], StringComparison.OrdinalIgnoreCase) &&
                (uri.AbsolutePath == value[value.IndexOf('/')..] || uri.AbsolutePath.StartsWith(value[value.IndexOf('/')..].TrimEnd('/') + "/", StringComparison.Ordinal)),
            _ => false
        };
    }
    public static CaptureDecision Decide(AppSettings settings, string url, string? fileName, long? size, bool privateWindow = false, string method = "GET")
    {
        if (string.IsNullOrWhiteSpace(fileName) && Uri.TryCreate(url, UriKind.Absolute, out var parsed)) fileName = Uri.UnescapeDataString(parsed.AbsolutePath);
        var category = Category(fileName);
        CaptureDecision Keep(string reason) => new("browser", reason, category);
        if (method != "GET") return Keep(CaptureCatalog.Reasons.Method);
        if (privateWindow && !settings.BrowserCapturePrivate) return Keep(CaptureCatalog.Reasons.Private);
        if (settings.BrowserExclusionRules.Any(r => Matches(r, url, fileName)) ||
            settings.BrowserExcludedSites.Any(h => Matches("host:" + h, url, fileName))) return Keep(CaptureCatalog.Reasons.Excluded);
        if (!settings.BrowserCaptureEnabled) return Keep(CaptureCatalog.Reasons.Disabled);
        if (size is > 0 && size < (long)settings.BrowserCaptureMinSizeKiB * 1024) return Keep(CaptureCatalog.Reasons.Minimum);
        var policy = settings.BrowserCapturePolicy ?? Migrate(settings.BrowserCaptureExtensions);
        var action = policy.Extensions?.GetValueOrDefault(Extension(fileName)) ?? policy.Categories?.GetValueOrDefault(category) ?? "ask";
        return new(IsAction(action) ? action : "browser", CaptureCatalog.Reasons.Preference, category);
    }
}

namespace Colibri.Core.Models;

/// <summary>
/// Helpers for HTTP header dictionaries. Header names are case-insensitive.
/// </summary>
public static class HttpHeaders
{
    // RFC 7230 "tchar": the characters allowed in a header name besides letters and digits.
    private const string TokenSymbols = "!#$%&'*+-.^_`|~";

    // Headers a browser may report that must not be passed on to the engine: aria2 sets them itself
    // (Range for segmented downloads, Host, Content-Length, ...), they describe the browser's own connection
    // (Connection, Keep-Alive, Upgrade, TE, Transfer-Encoding, Proxy-*), they would make the server send a
    // compressed body that aria2 writes to disk as is (Accept-Encoding), or they have dedicated fields
    // (Cookie, Referer, User-Agent).
    private static readonly HashSet<string> NotForwardable = new(StringComparer.OrdinalIgnoreCase)
    {
        "Range", "Accept-Encoding", "Content-Length", "Host", "Connection", "Transfer-Encoding", "Upgrade",
        "TE", "Keep-Alive", "Cookie", "Referer", "User-Agent",
    };

    /// <summary>Creates an empty, case-insensitive header dictionary.</summary>
    public static Dictionary<string, string> Create() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a case-insensitive copy of <paramref name="source"/>. Names that differ only in
    /// case are merged; the last one wins.
    /// </summary>
    public static Dictionary<string, string> Copy(IEnumerable<KeyValuePair<string, string>> source)
    {
        var copy = Create();
        foreach (var (name, value) in source)
        {
            copy[name] = value;
        }

        return copy;
    }

    /// <summary>Like <see cref="Copy"/>, but drops headers whose name or value is not valid.</summary>
    public static Dictionary<string, string> CopyValid(IEnumerable<KeyValuePair<string, string>> source) =>
        Copy(source.Where(h => IsValidName(h.Key) && IsValidValue(h.Value)));

    /// <summary>Whether <paramref name="name"/> is a valid header name (an RFC 7230 token).</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || TokenSymbols.Contains(c));

    /// <summary>
    /// Whether <paramref name="value"/> is safe to send as a header value: no CR, LF, NUL or other
    /// control characters (tab is allowed). This blocks header injection.
    /// </summary>
    public static bool IsValidValue(string? value) =>
        value is not null && value.All(c => c == '\t' || (c >= 0x20 && c != 0x7F));

    /// <summary>
    /// Whether a header captured from a browser may be passed on to the download engine. Headers the
    /// engine manages itself, connection-level headers, <c>Proxy-*</c>, and headers that have their own
    /// field (Cookie, Referer, User-Agent) are not forwarded.
    /// </summary>
    public static bool IsForwardable(string? name) =>
        IsValidName(name)
        && !NotForwardable.Contains(name!)
        && !name!.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns <paramref name="value"/> if it is a valid header value, otherwise null.</summary>
    public static string? ValidValueOrNull(string? value) => IsValidValue(value) ? value : null;
}

using System.Diagnostics.CodeAnalysis;

namespace Colibri.Core.Services;

/// <summary>
/// Decides which URLs Colibri accepts as downloads.
/// </summary>
public static class UrlPolicy
{
    /// <summary>Longest URL accepted, in characters.</summary>
    public const int MaxLength = 8192;

    private static readonly string[] AllowedSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeFtp];

    /// <summary>
    /// Accepts absolute http, https and ftp URLs with a host, up to <see cref="MaxLength"/> characters,
    /// with no spaces or control characters inside (leading and trailing whitespace is trimmed).
    /// Everything else (blob:, data:, file:, javascript:, relative URLs, ...) is rejected with an error message.
    /// User info (<c>user:pass@</c>) is accepted because FTP needs it; use <see cref="Redact"/> for logs.
    /// </summary>
    /// <remarks>
    /// Pass the URL on (to the engine, the database, ...) as <c>uri.AbsoluteUri</c>, which is fully
    /// escaped. Never use <c>ToString()</c> or <c>OriginalString</c>: they can contain unescaped characters.
    /// </remarks>
    public static bool TryValidate(string? url, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(false)] out string? error)
    {
        uri = null;
        url = url?.Trim();

        if (string.IsNullOrEmpty(url))
        {
            error = "The URL is empty.";
            return false;
        }

        if (url.Length > MaxLength)
        {
            error = $"The URL is longer than {MaxLength} characters.";
            return false;
        }

        // Uri would quietly escape or strip these; a real link never contains them unescaped.
        if (url.Any(c => c <= 0x20 || c == 0x7F))
        {
            error = "The URL contains spaces or control characters.";
            return false;
        }

        // Note: on Linux and macOS "/some/path" parses as an absolute file: URI; the scheme check rejects it.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = "The URL is not a valid absolute URL.";
            return false;
        }

        if (!AllowedSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            error = $"The '{parsed.Scheme}' scheme is not supported. Use http, https or ftp.";
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            error = "The URL has no host.";
            return false;
        }

        uri = parsed;
        error = null;
        return true;
    }

    /// <summary>
    /// Returns <c>scheme://host[:port]/path</c> without user info, query or fragment, which may hold
    /// passwords or tokens. Use this whenever a URL is written to a log.
    /// </summary>
    public static string Redact(Uri uri) =>
        uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
}

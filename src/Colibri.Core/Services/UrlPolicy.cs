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
        if (TryValidateWithReason(url, out uri, out var reason))
        {
            error = null;
            return true;
        }

        error = reason switch
        {
            UrlValidationError.Empty => "The URL is empty.",
            UrlValidationError.TooLong => $"The URL is longer than {MaxLength} characters.",
            UrlValidationError.InvalidCharacters => "The URL contains spaces or control characters.",
            UrlValidationError.NotAbsolute => "The URL is not a valid absolute URL.",
            UrlValidationError.UnsupportedScheme => $"The '{SchemeOf(url)}' scheme is not supported. Use http, https or ftp.",
            _ => "The URL has no host.",
        };
        return false;
    }

    /// <summary>
    /// Same rules as the other overload, but reports the reason as <see cref="UrlValidationError"/>
    /// so the UI can show a translated message.
    /// </summary>
    public static bool TryValidateWithReason(string? url, [NotNullWhen(true)] out Uri? uri, out UrlValidationError error)
    {
        uri = null;
        url = url?.Trim();

        if (string.IsNullOrEmpty(url))
        {
            error = UrlValidationError.Empty;
            return false;
        }

        if (url.Length > MaxLength)
        {
            error = UrlValidationError.TooLong;
            return false;
        }

        // Uri would quietly escape or strip these; a real link never contains them unescaped.
        if (url.Any(c => c <= 0x20 || c == 0x7F))
        {
            error = UrlValidationError.InvalidCharacters;
            return false;
        }

        // Note: on Linux and macOS "/some/path" parses as an absolute file: URI; the scheme check rejects it.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = UrlValidationError.NotAbsolute;
            return false;
        }

        if (!AllowedSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            error = UrlValidationError.UnsupportedScheme;
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            error = UrlValidationError.NoHost;
            return false;
        }

        uri = parsed;
        error = UrlValidationError.None;
        return true;
    }

    private static string SchemeOf(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) ? parsed.Scheme : string.Empty;

    /// <summary>
    /// Returns <c>scheme://host[:port]/path</c> without user info, query or fragment, which may hold
    /// passwords or tokens. Use this whenever a URL is written to a log.
    /// </summary>
    public static string Redact(Uri uri) =>
        uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
}

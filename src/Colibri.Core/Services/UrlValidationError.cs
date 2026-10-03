namespace Colibri.Core.Services;

/// <summary>
/// Why <see cref="UrlPolicy"/> rejected a URL. Lets the UI show its own (translated) message.
/// </summary>
public enum UrlValidationError
{
    None,
    Empty,
    TooLong,
    InvalidCharacters,
    NotAbsolute,
    UnsupportedScheme,
    NoHost,
}

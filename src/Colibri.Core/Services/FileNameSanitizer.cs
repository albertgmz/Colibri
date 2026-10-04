using System.Globalization;
using System.Text;

namespace Colibri.Core.Services;

/// <summary>
/// Turns untrusted file names (from URLs, servers or browsers) into names that are safe on
/// Windows, Linux and macOS, whatever OS the code runs on.
/// </summary>
public static class FileNameSanitizer
{
    /// <summary>
    /// Maximum file name length in UTF-8 bytes. File systems allow 255, but aria2 writes a
    /// "&lt;name&gt;.aria2" control file next to the download and <see cref="MakeUnique"/> may append
    /// " (n)", so names stay well below that limit.
    /// </summary>
    public const int MaxBytes = 240;

    private const string DefaultFallback = "download";

    private const char ZeroWidthNonJoiner = '‌';
    private const char ZeroWidthJoiner = '‍';

    // Characters Windows forbids in file names ('/' is also the separator on Linux and macOS).
    private static readonly HashSet<char> InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    // Windows device names; they are reserved even with an extension ("con.txt").
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// Returns a safe file name: directory parts, invalid, control and invisible format characters
    /// removed, Windows device names prefixed with "_", at most <see cref="MaxBytes"/> UTF-8 bytes
    /// (extension kept) and NFC-normalized. Falls back to <paramref name="fallback"/> when nothing
    /// visible is left; if only the part before the extension vanished, the fallback's name is used
    /// with the original extension (so "​.pdf" becomes "download.pdf", not a hidden ".pdf").
    /// </summary>
    public static string Sanitize(string? name, string fallback = DefaultFallback)
    {
        var fallbackName = Clean(fallback, DefaultFallback);
        if (fallbackName.Length == 0)
        {
            fallbackName = DefaultFallback;
        }

        var result = Clean(name, SplitExtension(fallbackName).Stem);
        return result.Length == 0 ? fallbackName : result;
    }

    /// <summary>
    /// Returns <paramref name="fileName"/> if neither it nor its aria2 control file
    /// ("&lt;name&gt;.aria2") exists in <paramref name="folder"/>; otherwise the first free name of the
    /// form "name (1).ext", "name (2).ext", ... The result never exceeds <see cref="MaxBytes"/>.
    /// </summary>
    /// <param name="folder">Target folder.</param>
    /// <param name="fileName">A name already passed through <see cref="Sanitize"/>.</param>
    /// <param name="exists">Returns true when the given full path is taken.</param>
    public static string MakeUnique(string folder, string fileName, Func<string, bool> exists)
    {
        var (stem, extension) = SplitForTruncation(fileName);
        const int maxAttempts = 10_000;
        for (var i = 0; i <= maxAttempts; i++)
        {
            var suffix = i == 0 ? string.Empty : $" ({i})";
            var budget = MaxBytes - Utf8Length(extension) - Utf8Length(suffix);
            var candidate = (TruncateToBytes(stem, Math.Max(0, budget)) + suffix + extension).TrimEnd('.', ' ');

            var path = Path.Combine(folder, candidate);
            if (!exists(path) && !exists(path + ".aria2"))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not find a free file name for '{fileName}' in '{folder}'.");
    }

    private static string Clean(string? name, string fallbackStem)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        // Unpaired surrogates must go first: string.Normalize throws on them.
        var text = DropUnpairedSurrogates(name).Normalize(NormalizationForm.FormC);

        // Keep only the last path part, so "../../etc/passwd" becomes "passwd". Both separators
        // are handled because the name may come from any OS.
        text = text[(text.LastIndexOfAny(['/', '\\']) + 1)..].Trim();
        var wasHiddenFile = text.StartsWith('.');

        text = TrimEnd(RemoveUnsafeCharacters(text).Trim());
        if (!HasVisibleCharacter(text))
        {
            return string.Empty;
        }

        // Removed characters must not turn "​.pdf" into the hidden file ".pdf".
        if (!wasHiddenFile && text.StartsWith('.'))
        {
            text = fallbackStem + text;
        }

        var (stem, extension) = SplitExtension(text);
        if (extension.Length > 0 && !HasVisibleCharacter(stem))
        {
            text = fallbackStem + extension;
        }

        text = Truncate(text, fallbackStem);
        if (!HasVisibleCharacter(text))
        {
            return string.Empty;
        }

        if (IsReserved(text))
        {
            text = Truncate("_" + text, fallbackStem);
        }

        return text;
    }

    private static string DropUnpairedSurrogates(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (!char.IsSurrogate(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string RemoveUnsafeCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || IsStrippedFormatCharacter(rune))
            {
                continue;
            }

            if (rune.IsBmp && InvalidChars.Contains((char)rune.Value))
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Invisible format characters (zero-width space, BOM, soft hyphen, direction marks such as
    /// U+202E which makes "invoice‮fdp.exe" display as "invoiceexe.pdf"). The joiners ZWJ and
    /// ZWNJ are kept because emoji sequences and scripts such as Persian need them.
    /// </summary>
    private static bool IsStrippedFormatCharacter(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
        && rune.Value != ZeroWidthJoiner
        && rune.Value != ZeroWidthNonJoiner;

    private static bool HasVisibleCharacter(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Format:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.Control:
                    continue;
                default:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Limits a name to <see cref="MaxBytes"/> UTF-8 bytes by shortening the part before the extension,
    /// then removes trailing dots and spaces again (the cut may expose them).
    /// </summary>
    private static string Truncate(string fileName, string fallbackStem)
    {
        if (Utf8Length(fileName) <= MaxBytes)
        {
            return fileName;
        }

        var (stem, extension) = SplitForTruncation(fileName);
        var budget = MaxBytes - Utf8Length(extension);
        var cutStem = TruncateToBytes(stem, budget);

        // A single huge character (e.g. a letter with hundreds of combining marks) cannot be cut.
        if (extension.Length > 0 && !HasVisibleCharacter(cutStem))
        {
            cutStem = TruncateToBytes(fallbackStem, budget);
        }

        return TrimEnd(cutStem + extension);
    }

    /// <summary>Like <see cref="SplitExtension"/>, but an over-long "extension" is treated as part of the name.</summary>
    private static (string Stem, string Extension) SplitForTruncation(string fileName)
    {
        var (stem, extension) = SplitExtension(fileName);
        return Utf8Length(extension) > MaxBytes / 2 ? (fileName, string.Empty) : (stem, extension);
    }

    /// <summary>Cuts text to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character.</summary>
    private static string TruncateToBytes(string text, int maxBytes)
    {
        var builder = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var size = Utf8Length(element);
            if (used + size > maxBytes)
            {
                break;
            }

            builder.Append(element);
            used += size;
        }

        return builder.ToString();
    }

    /// <summary>Splits "a.txt" into ("a", ".txt") and keeps ".tar.gz"-style pairs together.</summary>
    private static (string Stem, string Extension) SplitExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0)
        {
            return (fileName, string.Empty);
        }

        var stem = fileName[..dot];
        if (stem.Length > 4 && stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            dot -= 4;
        }

        return (fileName[..dot], fileName[dot..]);
    }

    private static bool IsReserved(string fileName)
    {
        // Windows checks the part before the first dot, ignoring trailing spaces ("CON .txt").
        var dot = fileName.IndexOf('.');
        var baseName = (dot < 0 ? fileName : fileName[..dot]).TrimEnd(' ');
        return ReservedNames.Contains(baseName);
    }

    // Windows silently drops trailing dots and spaces; this also turns "." and ".." into "".
    private static string TrimEnd(string text) => text.TrimEnd('.', ' ');

    private static int Utf8Length(string text) => Encoding.UTF8.GetByteCount(text);
}

using System.Text;
using Colibri.Core.Services;

namespace Colibri.Core.Tests.Services;

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("my file (1).zip", "my file (1).zip")]
    [InlineData(".bashrc", ".bashrc")]
    [InlineData("日本語ファイル.zip", "日本語ファイル.zip")]
    public void Keeps_safe_names(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\x.exe", "x.exe")]
    [InlineData("/absolute/path/file.txt", "file.txt")]
    [InlineData("C:\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("folder/sub\\mixed.bin", "mixed.bin")]
    [InlineData("\\\\server\\share\\file.doc", "file.doc")]
    public void Strips_directory_parts_and_traversal(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("Lpt1.log", "_Lpt1.log")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("aux", "_aux")]
    [InlineData("PRN.pdf", "_PRN.pdf")]
    [InlineData("com9.zip", "_com9.zip")]
    [InlineData("COM\u00B9.txt", "_COM\u00B9.txt")]
    [InlineData("CON .txt", "_CON .txt")]
    [InlineData("CON.", "_CON")]
    public void Prefixes_windows_reserved_names(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("CONSOLE.txt")]
    [InlineData("COM10.txt")]
    [InlineData("icon.png")]
    [InlineData("my.con.txt")]
    public void Leaves_names_that_only_look_reserved(string input)
    {
        Assert.Equal(input, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "abcdefgh.txt")]
    [InlineData("file\u0000name\u001F.txt", "filename.txt")]
    [InlineData("tab\there.txt", "tabhere.txt")]
    [InlineData("line\r\nbreak.txt", "linebreak.txt")]
    [InlineData("del\u007Fchar.txt", "delchar.txt")]
    [InlineData("invoice\u202Efdp.exe", "invoicefdp.exe")]
    public void Removes_invalid_and_control_characters(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("report.pdf.", "report.pdf")]
    [InlineData("report.pdf. . ", "report.pdf")]
    [InlineData("name   ", "name")]
    [InlineData("   leading.txt", "leading.txt")]
    public void Trims_trailing_dots_and_spaces(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("/")]
    [InlineData("../")]
    [InlineData("..\\..\\")]
    [InlineData("<>:\"|?*")]
    [InlineData("\u0001\u0002")]
    public void Falls_back_when_nothing_is_left(string? input)
    {
        Assert.Equal("download", FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Uses_the_given_fallback()
    {
        Assert.Equal("file.bin", FileNameSanitizer.Sanitize("..", "file.bin"));
    }

    [Fact]
    public void Unsafe_fallback_is_sanitized_too()
    {
        Assert.Equal("download", FileNameSanitizer.Sanitize("", "../.."));
    }

    [Fact]
    public void Normalizes_to_nfc()
    {
        var decomposed = "e\u0301te\u0301.txt"; // "été" with combining accents

        Assert.Equal("\u00E9t\u00E9.txt", FileNameSanitizer.Sanitize(decomposed));
    }

    [Theory]
    [InlineData("a", 300, ".pdf")]
    [InlineData("\u00E9", 200, ".mp4")] // 2 bytes each
    [InlineData("日", 120, ".zip")] // 3 bytes each
    [InlineData("\U0001F600", 100, ".txt")] // 4 bytes each (surrogate pair)
    [InlineData("a", 300, ".tar.gz")]
    public void Long_names_are_cut_to_240_bytes_keeping_the_extension(string unit, int count, string extension)
    {
        var input = string.Concat(Enumerable.Repeat(unit, count)) + extension;

        var result = FileNameSanitizer.Sanitize(input);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= 240, $"{Encoding.UTF8.GetByteCount(result)} bytes");
        Assert.EndsWith(extension, result);
        Assert.StartsWith(unit, result);
        // The cut must not split a character: every part before the extension is a whole unit.
        Assert.Equal(string.Empty, result[..^extension.Length].Replace(unit, string.Empty));
    }

    [Fact]
    public void Long_name_without_extension_is_cut()
    {
        var result = FileNameSanitizer.Sanitize(new string('x', 400));

        Assert.Equal(new string('x', 240), result);
    }

    [Fact]
    public void Long_reserved_looking_name_stays_within_the_limit()
    {
        var result = FileNameSanitizer.Sanitize("con." + new string('x', 400));

        Assert.StartsWith("_con.", result);
        Assert.True(Encoding.UTF8.GetByteCount(result) <= 240);
    }

    [Fact]
    public void MakeUnique_returns_the_name_when_it_is_free()
    {
        Assert.Equal("a.txt", FileNameSanitizer.MakeUnique("dir", "a.txt", _ => false));
    }

    [Fact]
    public void MakeUnique_appends_a_counter_before_the_extension()
    {
        var taken = Paths("dir", "a.txt", "a (1).txt");

        Assert.Equal("a (2).txt", FileNameSanitizer.MakeUnique("dir", "a.txt", taken.Contains));
    }

    [Theory]
    [InlineData("README", "README (1)")]
    [InlineData("linux.tar.gz", "linux (1).tar.gz")]
    [InlineData(".bashrc", ".bashrc (1)")]
    [InlineData("photo.final.jpg", "photo.final (1).jpg")]
    public void MakeUnique_handles_extension_shapes(string fileName, string expected)
    {
        var taken = Paths("dir", fileName);

        Assert.Equal(expected, FileNameSanitizer.MakeUnique("dir", fileName, taken.Contains));
    }

    [Fact]
    public void MakeUnique_passes_full_paths_to_exists()
    {
        var seen = new List<string>();

        FileNameSanitizer.MakeUnique("downloads", "a.txt", path =>
        {
            seen.Add(path);
            return seen.Count == 1;
        });

        Assert.Equal(Path.Combine("downloads", "a.txt"), seen[0]);
        Assert.Contains(Path.Combine("downloads", "a (1).txt"), seen);
    }

    [Fact]
    public void MakeUnique_keeps_long_names_within_the_limit()
    {
        var name = FileNameSanitizer.Sanitize(new string('a', 300) + ".pdf");
        var taken = Paths("dir", name);

        var result = FileNameSanitizer.MakeUnique("dir", name, taken.Contains);

        Assert.EndsWith(" (1).pdf", result);
        Assert.True(Encoding.UTF8.GetByteCount(result) <= 240);
    }

    // A [Fact] rather than [InlineData]: attribute strings are stored as UTF-8, which would
    // silently turn a lone surrogate into U+FFFD before the test even runs.
    [Fact]
    public void Drops_unpaired_surrogates_without_throwing()
    {
        Assert.Equal("abc.txt", FileNameSanitizer.Sanitize("\uD800abc.txt")); // lone high surrogate
        Assert.Equal("abc.txt", FileNameSanitizer.Sanitize("abc\uDC00.txt")); // lone low surrogate
        Assert.Equal("é.txt", FileNameSanitizer.Sanitize("é\uD800.txt")); // next to text that needs NFC
        Assert.Equal("download", FileNameSanitizer.Sanitize("\uD800"));
    }

    [Fact]
    public void Max_bytes_leaves_room_for_the_aria2_control_file_and_a_counter()
    {
        Assert.Equal(240, FileNameSanitizer.MaxBytes);
    }

    [Theory]
    [InlineData(239)] // the cut lands right after the space
    [InlineData(254)] // review repro
    public void No_trailing_space_after_truncation(int leadingCount)
    {
        var input = new string('x', leadingCount) + " " + "yyyyyyyyyy";

        var result = FileNameSanitizer.Sanitize(input);

        Assert.False(result.EndsWith(' '), $"'{result}' ends with a space");
        Assert.Equal(new string('x', Math.Min(leadingCount, 240)), result);
    }

    [Fact]
    public void No_trailing_dot_after_truncation()
    {
        var input = new string('x', 238) + ".." + new string('y', 200); // no real extension: it is too long

        var result = FileNameSanitizer.Sanitize(input);

        Assert.Equal(new string('x', 238), result);
    }

    [Theory]
    [InlineData("a​b.txt", "ab.txt")] // zero-width space
    [InlineData("﻿bom.txt", "bom.txt")] // byte order mark
    [InlineData("soft­hyphen.txt", "softhyphen.txt")]
    [InlineData("arabic؜mark.txt", "arabicmark.txt")]
    [InlineData("invoice‮fdp.exe", "invoicefdp.exe")]
    [InlineData("iso⁦late⁩.txt", "isolate.txt")]
    public void Removes_format_characters(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("\U0001F468‍\U0001F469.txt")] // ZWJ inside an emoji sequence
    [InlineData("می‌خواهم.txt")] // ZWNJ in Persian text
    public void Keeps_joiners(string input)
    {
        Assert.Equal(input, FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("​﻿")]
    [InlineData("‍")]
    [InlineData("‌‍ ")]
    public void Falls_back_when_nothing_visible_is_left(string input)
    {
        Assert.Equal("download", FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("conout$.txt", "_conout$.txt")]
    [InlineData("Clock$.log", "_Clock$.log")]
    public void Prefixes_console_and_clock_device_names(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Stem_cut_to_nothing_uses_the_fallback_name()
    {
        // One grapheme of ~600 bytes cannot be cut, so the whole stem goes.
        var input = "a" + string.Concat(Enumerable.Repeat("́", 300)) + ".exe";

        Assert.Equal("download.exe", FileNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("​.pdf", "download.pdf")]
    [InlineData("‍.txt", "download.txt")]
    public void Invisible_stem_uses_the_fallback_name(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Invisible_stem_uses_the_stem_of_a_custom_fallback()
    {
        Assert.Equal("file.pdf", FileNameSanitizer.Sanitize("​.pdf", "file.bin"));
    }

    [Fact]
    public void MakeUnique_cuts_an_over_long_free_name_to_the_limit()
    {
        var name = new string('a', 300) + ".pdf";

        var result = FileNameSanitizer.MakeUnique("dir", name, _ => false);

        Assert.EndsWith(".pdf", result);
        Assert.True(Encoding.UTF8.GetByteCount(result) <= 240);
    }

    [Fact]
    public void MakeUnique_treats_a_leftover_aria2_control_file_as_taken()
    {
        var taken = Paths("dir", "a.txt.aria2");

        Assert.Equal("a (1).txt", FileNameSanitizer.MakeUnique("dir", "a.txt", taken.Contains));
    }

    private static HashSet<string> Paths(string folder, params string[] names) =>
        names.Select(n => Path.Combine(folder, n)).ToHashSet();
}

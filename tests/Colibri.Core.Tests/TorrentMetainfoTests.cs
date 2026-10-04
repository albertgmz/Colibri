using System.Text;
using Colibri.Core.Torrents;

namespace Colibri.Core.Tests;

public sealed class TorrentMetainfoTests
{
    [Fact]
    public void V1_file_tree_and_selection_are_bounded_and_use_one_based_indices()
    {
        var parsed = TorrentMetainfo.Parse(Multi("a.txt", "folder/b.txt"));
        Assert.Equal(8, parsed.Metadata.TotalLength);
        Assert.Equal("root/a.txt", parsed.Metadata.Files[0].RelativePath);
        Assert.Equal("folder/b.txt", TorrentMetainfo.ContentRelativePath(parsed.Metadata, parsed.Metadata.Files[1]));
        Assert.Equal(new[] { 1, 2 }, TorrentMetainfo.ValidateSelection(parsed.Metadata, [2, 1]));
        Assert.Throws<ArgumentException>(() => TorrentMetainfo.ValidateSelection(parsed.Metadata, []));
        Assert.Throws<ArgumentException>(() => TorrentMetainfo.ValidateSelection(parsed.Metadata, [0]));
        Assert.Throws<ArgumentException>(() => TorrentMetainfo.ValidateSelection(parsed.Metadata, [1, 1]));
        Assert.Equal(40, parsed.Metadata.InfoHash.Length);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("CON.txt")]
    [InlineData("a.txt ")]
    [InlineData("a\\b.txt")]
    [InlineData("A.txt")]
    public void Unsafe_paths_and_case_collisions_are_rejected(string path) =>
        Assert.Throws<FormatException>(() => TorrentMetainfo.Parse(Multi("a.txt", path)));

    [Fact]
    public void Duplicate_bencode_keys_depth_and_unsafe_external_sources_are_rejected()
    {
        Assert.Throws<FormatException>(() => TorrentMetainfo.Parse(Encoding.ASCII.GetBytes("d4:infoi1e4:infoi2ee")));
        Assert.Throws<FormatException>(() => TorrentMetainfo.Parse(Encoding.ASCII.GetBytes(new string('l', 42) + new string('e', 42))));
        var valid = Single();
        var tail = Encoding.ASCII.GetString(valid)[1..];
        Assert.Throws<FormatException>(() => TorrentMetainfo.Parse(Encoding.ASCII.GetBytes("d8:announce" + String("file:///secret") + tail)));
        Assert.Throws<FormatException>(() => TorrentMetainfo.Parse(new byte[TorrentMetainfo.MaxMetainfoBytes + 1]));
    }

    [Fact]
    public void Magnets_normalize_both_hash_forms_and_reject_unsupported_sources()
    {
        var hex = new string('0', 40);
        Assert.Equal(hex, MagnetLink.Parse("magnet:?xt=urn:btih:" + hex).InfoHash);
        Assert.Equal(hex, MagnetLink.Parse("magnet:?xt=urn:btih:" + new string('A', 32)).InfoHash);
        Assert.Throws<FormatException>(() => MagnetLink.Parse("magnet:?xt=urn:btih:" + hex + "&x.pe=peer.test"));
        Assert.Throws<FormatException>(() => MagnetLink.Parse("magnet:?xt=urn:btih:" + hex + "&tr=%zz"));
        Assert.Throws<FormatException>(() => MagnetLink.Parse("magnet:?xt=urn:btih:" + hex + "&tr=file:///secret"));
        Assert.Throws<FormatException>(() => MagnetLink.Parse("magnet:?xt=urn:btih:" + hex + "&xt=urn:btih:" + new string('1', 40)));
    }

    private static string String(string value) => Encoding.UTF8.GetByteCount(value) + ":" + value;
    private static byte[] Single() => Encoding.ASCII.GetBytes("d4:infod6:lengthi3e4:name5:a.txt12:piece lengthi16384e6:pieces20:12345678901234567890ee");
    private static byte[] Multi(string first, string second)
    {
        string File(string path, int length) => "d6:lengthi" + length + "e4:pathl" + string.Concat(path.Split('/').Select(String)) + "ee";
        return Encoding.UTF8.GetBytes("d4:infod5:filesl" + File(first, 3) + File(second, 5)
            + "e4:name4:root12:piece lengthi16384e6:pieces20:12345678901234567890ee");
    }
}

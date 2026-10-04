using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Colibri.Core.Torrents;

/// <summary>Bounded, canonical v1 metainfo parser. Never writes files or follows metadata links.</summary>
public static class TorrentMetainfo
{
    public const int MaxMetainfoBytes = 2 * 1024 * 1024;
    public const int MaxFiles = 10000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static TorrentMetadataResult Parse(byte[] metainfo)
    {
        if (metainfo.Length is 0 or > MaxMetainfoBytes) throw Invalid();
        try
        {
            var reader = new Reader(metainfo);
            var root = Dictionary(reader.Read(0));
            if (reader.Position != metainfo.Length || !root.TryGetValue("info", out var infoNode)) throw Invalid();
            ValidateSources(root);
            var info = Dictionary(infoNode);
            RejectSymlink(info);
            if (info.ContainsKey("meta version") || info.ContainsKey("file tree"))
                throw new FormatException("BitTorrent v2 metainfo is unsupported.");
            var name = Text(Required(info, "name"));
            ValidateSegment(name);
            if (info.TryGetValue("name.utf-8", out var alternateName) && Text(alternateName) != name) throw Invalid();
            var pieceLength = Integer(Required(info, "piece length"));
            if (pieceLength is <= 0 or > 64 * 1024 * 1024) throw Invalid();
            var pieces = Bytes(Required(info, "pieces"));
            if (pieces.Length % 20 != 0) throw Invalid();
            var files = new List<TorrentFile>();
            long total = 0;
            if (info.TryGetValue("files", out var fileList))
            {
                if (info.ContainsKey("length")) throw Invalid();
                var entries = List(fileList);
                if (entries.Count is 0 or > MaxFiles) throw Invalid();
                foreach (var entry in entries)
                {
                    var file = Dictionary(entry);
                    RejectSymlink(file);
                    var components = List(Required(file, "path")).Select(Text).ToArray();
                    if (components.Length is 0 or > 32) throw Invalid();
                    foreach (var component in components) ValidateSegment(component);
                    if (file.TryGetValue("path.utf-8", out var alternatePath)
                        && !List(alternatePath).Select(Text).SequenceEqual(components)) throw Invalid();
                    var path = name + "/" + string.Join('/', components);
                    if (path.Length > 4096) throw Invalid();
                    AddFile(path, Integer(Required(file, "length")));
                }
            }
            else
            {
                RejectSymlink(info);
                AddFile(name, Integer(Required(info, "length")));
            }
            if (pieces.Length / 20 != total / pieceLength + (total % pieceLength == 0 ? 0 : 1)) throw Invalid();
            var paths = files.Select(file => file.RelativePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var index = 1; index < paths.Length; index++)
                if (paths[index].Equals(paths[index - 1], StringComparison.OrdinalIgnoreCase)
                    || paths[index].StartsWith(paths[index - 1] + "/", StringComparison.OrdinalIgnoreCase)) throw Invalid();
            var privateValue = info.TryGetValue("private", out var privateNode) ? Integer(privateNode) : 0;
            if (privateValue is not (0 or 1)) throw Invalid();
            var isPrivate = privateValue == 1;
            // SHA-1 is the v1 protocol identity, not a cryptographic authenticity guarantee.
            var hash = Convert.ToHexString(SHA1.HashData(metainfo.AsSpan(infoNode.Start, infoNode.End - infoNode.Start))).ToLowerInvariant();
            return new(new(hash, name, files, total, isPrivate), metainfo.ToArray());

            void AddFile(string path, long length)
            {
                if (length < 0) throw Invalid();
                total = checked(total + length);
                files.Add(new(files.Count + 1, path, length));
            }
        }
        catch (Exception ex) when (ex is DecoderFallbackException or OverflowException)
        { throw Invalid(); }
    }

    public static int[] ValidateSelection(TorrentMetadata metadata, IReadOnlyList<int> indices)
    {
        if (indices.Count is 0 or > MaxFiles) throw new ArgumentException("Select at least one torrent file.");
        var sorted = indices.Distinct().Order().ToArray();
        if (sorted.Length != indices.Count || sorted.Any(index => index < 1 || index > metadata.Files.Count))
            throw new ArgumentException("Invalid torrent file selection.");
        return sorted;
    }

    /// <summary>Path within the manager's reserved content directory; never includes the torrent root.</summary>
    public static string ContentRelativePath(TorrentMetadata metadata, TorrentFile file) =>
        file.RelativePath.Contains('/') ? file.RelativePath[(metadata.Name.Length + 1)..] : file.RelativePath;

    private static void ValidateSegment(string name)
    {
        if (name.Length is 0 or > 240 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.')
            || name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character))
            || name != Colibri.Core.Services.FileNameSanitizer.Sanitize(name)) throw Invalid();
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')) throw Invalid();
    }

    private static void RejectSymlink(Dictionary<string, Node> values)
    {
        if (values.ContainsKey("symlink path") || (values.TryGetValue("attr", out var attr) && Text(attr).Contains('l'))) throw Invalid();
    }

    private static void ValidateSources(Dictionary<string, Node> root)
    {
        var count = 0;
        if (root.TryGetValue("announce", out var announce)) ValidateUri(Text(announce), tracker: true);
        if (root.TryGetValue("announce-list", out var tiers))
        {
            if (List(tiers).Count > 64) throw Invalid();
            foreach (var tier in List(tiers))
                foreach (var tracker in List(tier)) ValidateUri(Text(tracker), tracker: true);
        }
        foreach (var key in new[] { "url-list", "httpseeds" })
            if (root.TryGetValue(key, out var seeds))
            {
                IEnumerable<Node> seedList = seeds.Value is byte[] ? [seeds] : List(seeds);
                foreach (var seed in seedList)
                    ValidateUri(Text(seed), tracker: false);
            }

        void ValidateUri(string value, bool tracker)
        {
            if (++count > 128 || value.Length > 8192 || value.Any(char.IsControl)
                || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0
                || uri.UserInfo.Length != 0 || uri.Port is < 1 or > 65535
                || (tracker ? uri.Scheme is not ("http" or "https" or "udp") : uri.Scheme is not ("http" or "https"))) throw Invalid();
        }
    }
    private static Node Required(Dictionary<string, Node> dictionary, string key) => dictionary.TryGetValue(key, out var node) ? node : throw Invalid();
    private static Dictionary<string, Node> Dictionary(Node node) => node.Value as Dictionary<string, Node> ?? throw Invalid();
    private static List<Node> List(Node node) => node.Value as List<Node> ?? throw Invalid();
    private static byte[] Bytes(Node node) => node.Value as byte[] ?? throw Invalid();
    private static string Text(Node node) => Utf8.GetString(Bytes(node));
    private static long Integer(Node node) => node.Value is long value ? value : throw Invalid();
    private static FormatException Invalid() => new("Invalid, unsafe or oversized torrent metadata.");
    private sealed record Node(object Value, int Start, int End);

    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }
        private int _nodes;
        public Node Read(int depth)
        {
            if (depth > 40 || ++_nodes > 100000 || Position >= data.Length) throw Invalid();
            var start = Position;
            var token = data[Position];
            object value;
            if (token == 'd')
            {
                Position++;
                var dictionary = new Dictionary<string, Node>(StringComparer.Ordinal);
                byte[]? previous = null;
                while (!End())
                {
                    var keyBytes = Bytes(Read(depth + 1));
                    if (previous is not null && previous.AsSpan().SequenceCompareTo(keyBytes) >= 0) throw Invalid();
                    var key = Utf8.GetString(keyBytes);
                    if (key.Length > 1024 || !dictionary.TryAdd(key, Read(depth + 1))) throw Invalid();
                    previous = keyBytes;
                }
                value = dictionary;
            }
            else if (token == 'l')
            {
                Position++;
                var list = new List<Node>();
                while (!End()) list.Add(Read(depth + 1));
                value = list;
            }
            else if (token == 'i')
            {
                Position++;
                var numberStart = Position;
                while (Position < data.Length && data[Position] != 'e') Position++;
                if (Position == data.Length || Position - numberStart is 0 or > 20) throw Invalid();
                var number = Encoding.ASCII.GetString(data, numberStart, Position - numberStart);
                if (number == "-0" || (number.Length > 1 && number[0] == '0') || number.StartsWith("-0", StringComparison.Ordinal)
                    || !long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    || number[0] == '+') throw Invalid();
                Position++;
                value = parsed;
            }
            else if (token is >= (byte)'0' and <= (byte)'9')
            {
                var numberStart = Position;
                while (Position < data.Length && data[Position] is >= (byte)'0' and <= (byte)'9') Position++;
                if (Position == data.Length || data[Position] != ':' || Position - numberStart > 8
                    || (Position - numberStart > 1 && data[numberStart] == '0')) throw Invalid();
                if (!int.TryParse(Encoding.ASCII.GetString(data, numberStart, Position - numberStart), out var length)) throw Invalid();
                Position++;
                if (length > data.Length - Position) throw Invalid();
                value = data.AsSpan(Position, length).ToArray();
                Position += length;
            }
            else throw Invalid();
            return new(value, start, Position);
        }
        private bool End()
        {
            if (Position >= data.Length) throw Invalid();
            if (data[Position] != 'e') return false;
            Position++;
            return true;
        }
    }
}

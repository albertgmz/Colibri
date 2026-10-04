namespace Colibri.Core.Torrents;

public sealed record MagnetLink(string Uri, string InfoHash)
{
    public bool HasHttpTrackers { get; init; }
    public static MagnetLink Parse(string value)
    {
        if (value.Length is < 10 or > 32768 || value.Any(char.IsControl)
            || !value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || value.Contains('#'))
            throw new FormatException("Invalid or oversized magnet link.");
        for (var index = 0; index < value.Length; index++)
            if (value[index] == '%' && (index + 2 >= value.Length || !System.Uri.IsHexDigit(value[index + 1]) || !System.Uri.IsHexDigit(value[index + 2])))
                throw new FormatException("Malformed magnet escape.");
        var fields = value[8..].Split('&');
        if (fields.Length > 256) throw new FormatException("Too many magnet parameters.");
        string? hash = null;
        var trackers = 0;
        var hasHttpTrackers = false;
        var normalized = new List<string>();
        foreach (var field in fields)
        {
            var parts = field.Split('=', 2);
            if (parts.Length != 2) throw new FormatException("Invalid magnet parameter.");
            var key = System.Uri.UnescapeDataString(parts[0]).ToLowerInvariant();
            var text = System.Uri.UnescapeDataString(parts[1]);
            if (key.Any(char.IsControl) || text.Any(char.IsControl) || key is not ("xt" or "dn" or "tr" or "xl" or "kt"))
                throw new FormatException("Unsupported or invalid magnet parameter.");
            if (key.Equals("xt", StringComparison.OrdinalIgnoreCase))
            {
                if (!text.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Only BitTorrent v1 magnet links are supported.");
                var candidate = NormalizeHash(text[9..]);
                if (hash is not null && hash != candidate) throw new FormatException("Conflicting magnet hashes.");
                hash = candidate;
                text = "urn:btih:" + candidate;
            }
            // aria2 may use these as arbitrary file sources or destinations. Metadata discovery
            // is limited to trackers/DHT; never delegate web seeds, exact sources or selection here.
            else if (key.Equals("tr", StringComparison.OrdinalIgnoreCase)
                && (++trackers > 64 || text.Length > 8192 || !System.Uri.TryCreate(text, UriKind.Absolute, out var tracker)
                    || tracker.Scheme is not ("http" or "https" or "udp") || tracker.Host.Length == 0
                    || tracker.UserInfo.Length != 0 || tracker.Port is < 1 or > 65535))
                throw new FormatException("Invalid magnet tracker.");
            else if (key is "dn" or "kt" && text.Length > 1024)
                throw new FormatException("Oversized magnet parameter.");
            else if (key == "xl" && !long.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _)) throw new FormatException("Invalid magnet size.");
            normalized.Add(key + "=" + System.Uri.EscapeDataString(text));
            if (key == "tr" && System.Uri.TryCreate(text, UriKind.Absolute, out var parsedTracker)
                && parsedTracker.Scheme is "http" or "https") hasHttpTrackers = true;
        }
        return new("magnet:?" + string.Join('&', normalized), hash ?? throw new FormatException("Magnet link has no BitTorrent info hash."))
        { HasHttpTrackers = hasHttpTrackers };
    }

    public override string ToString() => $"MagnetLink {{ InfoHash = {InfoHash} }}";

    private static string NormalizeHash(string hash)
    {
        if (hash.Length == 40 && hash.All(System.Uri.IsHexDigit)) return hash.ToLowerInvariant();
        if (hash.Length != 32) throw new FormatException("Invalid magnet info hash.");
        var output = new byte[20];
        uint buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var character in hash.ToUpperInvariant())
        {
            var digit = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(character);
            if (digit < 0) throw new FormatException("Invalid magnet info hash.");
            buffer = (buffer << 5) | (uint)digit;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            output[index++] = (byte)(buffer >> bits);
        }
        return Convert.ToHexString(output).ToLowerInvariant();
    }
}

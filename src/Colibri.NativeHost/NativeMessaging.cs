using System.Buffers.Binary;

namespace Colibri.NativeHost;

/// <summary>What <see cref="NativeMessaging.ReadAsync"/> found.</summary>
internal enum FrameStatus
{
    Message,

    /// <summary>The browser closed stdin between two messages: the normal way to end.</summary>
    EndOfStream,

    /// <summary>The length prefix announces more than the limit; the body is not read.</summary>
    TooLarge,

    /// <summary>The stream ended in the middle of a length prefix or a body.</summary>
    Truncated,
}

/// <summary>
/// Chrome's native-messaging framing, used in both directions: a 32-bit unsigned length in the machine's
/// native byte order, then that many bytes of UTF-8 JSON.
/// </summary>
internal static class NativeMessaging
{
    /// <summary>Largest message accepted in either direction (Chrome's own limit from host to browser is 1 MB).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>Reads one message. Partial reads are completed; a closed stream before any byte is <see cref="FrameStatus.EndOfStream"/>.</summary>
    public static async Task<(FrameStatus Status, byte[]? Body)> ReadAsync(Stream input, int maxBytes, CancellationToken ct)
    {
        var prefix = new byte[4];
        var read = await input.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, ct);
        if (read == 0)
        {
            return (FrameStatus.EndOfStream, null);
        }

        if (read < prefix.Length)
        {
            return (FrameStatus.Truncated, null);
        }

        var length = ReadLength(prefix);
        if (length > (uint)maxBytes)
        {
            return (FrameStatus.TooLarge, null);
        }

        var body = new byte[length];
        read = await input.ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, ct);
        return read < body.Length ? (FrameStatus.Truncated, null) : (FrameStatus.Message, body);
    }

    /// <summary>Writes one message (length prefix and body in a single write) and flushes.</summary>
    public static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (body.Length > MaxMessageBytes)
        {
            throw new ArgumentException("The message is too large for the browser.", nameof(body));
        }

        var frame = new byte[4 + body.Length];
        WriteLength(frame, (uint)body.Length);
        body.CopyTo(frame.AsMemory(4));
        await output.WriteAsync(frame, ct);
        await output.FlushAsync(ct);
    }

    // The browser uses the byte order of the machine it runs on ("native byte order" in Chrome's docs), which
    // is the same machine as this host: little-endian on x64 and arm64, big-endian only on exotic hardware.
    private static uint ReadLength(ReadOnlySpan<byte> prefix) =>
        BitConverter.IsLittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(prefix) : BinaryPrimitives.ReadUInt32BigEndian(prefix);

    private static void WriteLength(Span<byte> prefix, uint length)
    {
        if (BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(prefix, length);
        }
    }
}

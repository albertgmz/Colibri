using System.Buffers.Binary;
using System.Text;

namespace Colibri.NativeHost.Tests;

public class NativeMessagingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A frame as the browser writes it: length in native byte order (little-endian here), then the body.</summary>
    internal static byte[] Frame(string json) => Frame(Encoding.UTF8.GetBytes(json));

    internal static byte[] Frame(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public void Tests_run_on_a_little_endian_machine()
    {
        // The frames in these tests are built little-endian, the native order of every machine the tests run on.
        Assert.True(BitConverter.IsLittleEndian);
    }

    [Fact]
    public async Task Reads_one_message_and_then_the_end()
    {
        var input = new MemoryStream(Frame("""{"type":"ping"}"""));

        var (status, body) = await NativeMessaging.ReadAsync(input, NativeMessaging.MaxMessageBytes, Ct);
        Assert.Equal(FrameStatus.Message, status);
        Assert.Equal("""{"type":"ping"}""", Encoding.UTF8.GetString(body!));

        (status, _) = await NativeMessaging.ReadAsync(input, NativeMessaging.MaxMessageBytes, Ct);
        Assert.Equal(FrameStatus.EndOfStream, status);
    }

    [Fact]
    public async Task Partial_reads_are_completed()
    {
        var input = new OneByteAtATimeStream(Frame("""{"type":"config"}""").Concat(Frame("{}")).ToArray());

        var (status, body) = await NativeMessaging.ReadAsync(input, 100, Ct);
        Assert.Equal((FrameStatus.Message, """{"type":"config"}"""), (status, Encoding.UTF8.GetString(body!)));
        (status, body) = await NativeMessaging.ReadAsync(input, 100, Ct);
        Assert.Equal((FrameStatus.Message, "{}"), (status, Encoding.UTF8.GetString(body!)));
    }

    [Theory]
    [InlineData(new byte[] { 5, 0 })]
    [InlineData(new byte[] { 5, 0, 0, 0, (byte)'a', (byte)'b' })]
    public async Task A_stream_ending_inside_a_frame_is_truncated(byte[] data)
    {
        var (status, _) = await NativeMessaging.ReadAsync(new MemoryStream(data), 100, Ct);

        Assert.Equal(FrameStatus.Truncated, status);
    }

    [Fact]
    public async Task An_oversized_length_is_refused_without_reading_the_body()
    {
        var input = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3]);

        var (status, _) = await NativeMessaging.ReadAsync(input, NativeMessaging.MaxMessageBytes, Ct);

        Assert.Equal(FrameStatus.TooLarge, status);
        Assert.Equal(4, input.Position);
    }

    [Fact]
    public async Task A_message_of_exactly_the_limit_is_accepted()
    {
        var (status, body) = await NativeMessaging.ReadAsync(new MemoryStream(Frame(new byte[100])), 100, Ct);

        Assert.Equal(FrameStatus.Message, status);
        Assert.Equal(100, body!.Length);
    }

    [Fact]
    public async Task Writes_the_length_in_native_byte_order()
    {
        var output = new MemoryStream();

        await NativeMessaging.WriteAsync(output, """{"ok":true}"""u8.ToArray(), Ct);

        Assert.Equal(Frame("""{"ok":true}"""), output.ToArray());
    }

    [Fact]
    public async Task Refuses_to_write_more_than_the_browser_accepts()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => NativeMessaging.WriteAsync(new MemoryStream(), new byte[NativeMessaging.MaxMessageBytes + 1], Ct));
    }

    /// <summary>Returns at most one byte per read, like a pipe delivering a message in pieces.</summary>
    internal sealed class OneByteAtATimeStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}

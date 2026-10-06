using System.Net.WebSockets;
using System.Text;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>PR #140 R4: <see cref="WebSocketFrameReader"/> must cap reassembled message size the
/// same way aiohttp's <c>WebSocketResponse</c> does (default 4 MiB) instead of growing its
/// <c>ArrayBufferWriter</c> without bound for a client that streams one endless fragmented
/// message.</summary>
public sealed class WebSocketFrameReaderTests
{
    [Fact]
    public async Task ReadMessageAsync_ReassemblesMultipleChunksUnderTheCap()
    {
        var part1 = Encoding.UTF8.GetBytes("{\"type\":\"input_audio_buffer.appen");
        var part2 = Encoding.UTF8.GetBytes("d\",\"audio\":\"AAAA\"}");
        var socket = new FakeWebSocket(new[]
        {
            (part1, false, WebSocketMessageType.Text),
            (part2, true, WebSocketMessageType.Text),
        });

        var frame = await WebSocketFrameReader.ReadMessageAsync(socket, CancellationToken.None, maxMessageBytes: 1024);

        Assert.NotNull(frame);
        Assert.Equal("{\"type\":\"input_audio_buffer.append\",\"audio\":\"AAAA\"}", Encoding.UTF8.GetString(frame!.Payload));
        Assert.False(socket.CloseOutputCalled);
    }

    [Fact]
    public async Task ReadMessageAsync_ReturnsNull_OnCloseFrame()
    {
        var socket = new FakeWebSocket(new[]
        {
            (Array.Empty<byte>(), true, WebSocketMessageType.Close),
        });

        var frame = await WebSocketFrameReader.ReadMessageAsync(socket, CancellationToken.None, maxMessageBytes: 1024);

        Assert.Null(frame);
        Assert.False(socket.CloseOutputCalled);
    }

    [Fact]
    public async Task ReadMessageAsync_ClosesWithMessageTooBig_WhenCumulativeSizeExceedsCap()
    {
        // Cap of 16 bytes reached by the second 10-byte chunk (10 + 10 > 16); the third chunk must
        // never be requested.
        var socket = new FakeWebSocket(new[]
        {
            (new byte[10], false, WebSocketMessageType.Text),
            (new byte[10], false, WebSocketMessageType.Text),
            (new byte[10], true, WebSocketMessageType.Text),
        });

        var frame = await WebSocketFrameReader.ReadMessageAsync(socket, CancellationToken.None, maxMessageBytes: 16);

        Assert.Null(frame);
        Assert.True(socket.CloseOutputCalled);
        Assert.False(socket.CloseCalled, "must use CloseOutputAsync, not the full CloseAsync handshake, so an unresponsive over-cap sender can't block the reader.");
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.ClosedWithStatus);
    }

    [Fact]
    public async Task ReadMessageAsync_ClosesWithMessageTooBig_WhenExactlyOneByteOverTheCap()
    {
        var socket = new FakeWebSocket(new[]
        {
            (new byte[17], true, WebSocketMessageType.Text),
        });

        var frame = await WebSocketFrameReader.ReadMessageAsync(socket, CancellationToken.None, maxMessageBytes: 16);

        Assert.Null(frame);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.ClosedWithStatus);
    }

    [Fact]
    public async Task ReadMessageAsync_AllowsExactlyTheCap()
    {
        var socket = new FakeWebSocket(new[]
        {
            (new byte[16], true, WebSocketMessageType.Text),
        });

        var frame = await WebSocketFrameReader.ReadMessageAsync(socket, CancellationToken.None, maxMessageBytes: 16);

        Assert.NotNull(frame);
        Assert.Equal(16, frame!.Payload.Length);
        Assert.False(socket.CloseOutputCalled);
    }

    [Fact]
    public void DefaultMaxMessageBytes_Is4MiB_MatchingAiohttpsMaxMsgSizeDefault()
    {
        Assert.Equal(4 * 1024 * 1024, WebSocketFrameReader.DefaultMaxMessageBytes);
    }
}

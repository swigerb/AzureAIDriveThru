using System.Buffers;
using System.Net.WebSockets;

namespace Backend.Realtime;

/// <summary>One fully-reassembled WebSocket message (text or binary), never a partial frame.</summary>
public sealed record WebSocketFrame(byte[] Payload, WebSocketMessageType MessageType);

/// <summary>
/// Issue #13 (Rick's #12 review note): reads ONE complete WebSocket message off a socket,
/// looping <c>ReceiveAsync</c> until <c>EndOfMessage</c> is true and accumulating every chunk.
/// Program.cs's original `/realtime` receive loop (issue #12/#75) did a single `ReceiveAsync`
/// into a fixed 4096-byte buffer and posted whatever came back as one frame -- silently
/// truncating/misdelivering any browser or upstream message spanning multiple WebSocket frames or
/// exceeding 4096 bytes (a real, latent bug: browser `session.update`/tool-result frames and
/// upstream `response.*` frames can both legitimately be larger than one TCP-sized chunk). Used
/// for BOTH directions of the realtime relay (browser&lt;-&gt;RealtimeProcessor and
/// RealtimeProcessor&lt;-&gt;upstream) since both sockets are equally exposed to fragmentation.
/// </summary>
public static class WebSocketFrameReader
{
    private const int ChunkSize = 8192;

    /// <summary>
    /// Reads and reassembles the next complete message. Returns null if the socket sent a Close
    /// frame (the caller decides how to react -- e.g. echoing a normal closure back) instead of
    /// throwing, since a clean close is an expected, not exceptional, outcome of this loop.
    /// </summary>
    public static async Task<WebSocketFrame?> ReadMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var chunk = new byte[ChunkSize];
        WebSocketReceiveResult result;
        // ArrayBufferWriter, not a fixed buffer: an individual message can be arbitrarily larger
        // than one chunk (unlike the byte[4096] this replaces, which silently dropped the excess).
        var writer = new ArrayBufferWriter<byte>();

        do
        {
            result = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.Count > 0)
            {
                writer.Write(chunk.AsSpan(0, result.Count));
            }
        } while (!result.EndOfMessage);

        return new WebSocketFrame(writer.WrittenSpan.ToArray(), result.MessageType);
    }
}

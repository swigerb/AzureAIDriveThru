using System.Buffers;
using System.Net.WebSockets;

namespace Backend.Realtime;

/// <summary>One fully-reassembled WebSocket message (text or binary), never a partial frame.</summary>
internal sealed record WebSocketFrame(byte[] Payload, WebSocketMessageType MessageType);

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
///
/// PR #140 R4: reassembly is capped at <see cref="DefaultMaxMessageBytes"/> (4 MiB, matching
/// aiohttp's <c>WebSocketResponse</c> default `max_msg_size`) so a client holding a valid session
/// token can't stream one endless fragmented message and exhaust memory -- exceeding the cap
/// closes the socket with <see cref="WebSocketCloseStatus.MessageTooBig"/> (1009) instead of
/// growing the buffer without bound.
/// </summary>
internal static class WebSocketFrameReader
{
    private const int ChunkSize = 8192;

    /// <summary>PR #140 R4: aiohttp's <c>WebSocketResponse</c> defaults to <c>max_msg_size</c> 4
    /// MiB and closes with 1009 (message too big) once a peer exceeds it. Without an equivalent
    /// cap here, a client holding a valid session token could stream one endless fragmented
    /// message and exhaust memory, since <see cref="ArrayBufferWriter{T}"/> grows without
    /// bound.</summary>
    public const int DefaultMaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads and reassembles the next complete message. Returns null if the socket sent a Close
    /// frame (the caller decides how to react -- e.g. echoing a normal closure back) instead of
    /// throwing, since a clean close is an expected, not exceptional, outcome of this loop. Also
    /// returns null -- after closing the socket with <see cref="WebSocketCloseStatus.MessageTooBig"/>
    /// -- if the reassembled message would exceed <paramref name="maxMessageBytes"/>.
    /// </summary>
    public static async Task<WebSocketFrame?> ReadMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken,
        int maxMessageBytes = DefaultMaxMessageBytes)
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
                if (writer.WrittenCount + result.Count > maxMessageBytes)
                {
                    await CloseMessageTooBigAsync(socket).ConfigureAwait(false);
                    return null;
                }
                writer.Write(chunk.AsSpan(0, result.Count));
            }
        } while (!result.EndOfMessage);

        return new WebSocketFrame(writer.WrittenSpan.ToArray(), result.MessageType);
    }

    private static async Task CloseMessageTooBigAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // CloseOutputAsync, not CloseAsync: send the 1009 close frame and stop reading
                // immediately, without waiting for the peer's own close handshake response -- a
                // client that is still streaming an oversized message past the cap has no reason
                // to be trusted to answer a close handshake promptly, and this reader must not
                // block waiting for one.
                await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Best-effort: the peer may have already torn the connection down, and the caller
            // treats a null return as "socket is gone" either way.
        }
    }
}

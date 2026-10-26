using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;

namespace Backend.Realtime;

/// <summary>
/// Guarantees that the Close frame is the last frame written to the browser socket.
/// </summary>
/// <remarks>
/// <see cref="WebSocket.SendAsync(ArraySegment{byte}, WebSocketMessageType, bool, CancellationToken)"/>
/// checks <see cref="WebSocket.State"/> once, when it is called, and the close only moves the state
/// to <see cref="WebSocketState.CloseSent"/> after the Close frame has been written. A data send that
/// passed the check while a close was already queued behind another write is therefore written
/// <em>after</em> the Close frame, and the browser reports "Data frame received after close".
/// This decorator closes that window. A close first refuses every later data send and then waits for
/// the one write that may be in flight, so no data frame can follow the Close frame.
/// <para>
/// A refused send completes silently, which is what <c>FramePump.SendTextAsync</c> and
/// <c>FramePump.SendBytesAsync</c> already do for a socket that is not <see cref="WebSocketState.Open"/>.
/// The refusal is permanent: a socket that has begun to close never carries data again.
/// Receives, <see cref="Abort"/> and the state properties pass straight through. Only the browser
/// socket is wrapped; the upstream socket carries the audio hot path and has no such race.
/// </para>
/// </remarks>
internal sealed class OrderedCloseWebSocket(WebSocket inner) : WebSocket
{
    // Never disposed on purpose: the semaphore owns no OS handle (its wait handle is never requested),
    // and disposing it would turn a send or close that races teardown into an ObjectDisposedException.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The gate holds no OS resource and must stay usable by a late send or close.")]
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private volatile bool _closing;

    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;

    public override string? CloseStatusDescription => inner.CloseStatusDescription;

    public override WebSocketState State => inner.State;

    public override string? SubProtocol => inner.SubProtocol;

    public override void Abort() => inner.Abort();

    // The caller retains ownership of the wrapped socket.
    public override void Dispose()
    {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        => inner.ReceiveAsync(buffer, cancellationToken);

    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => inner.ReceiveAsync(buffer, cancellationToken);

    public override async Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (!await TryEnterSendAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public override async ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (!await TryEnterSendAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public override async ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken)
    {
        if (!await TryEnterSendAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await inner.SendAsync(buffer, messageType, messageFlags, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        await WaitForInFlightSendAsync(cancellationToken).ConfigureAwait(false);
        await inner.CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
    }

    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        await WaitForInFlightSendAsync(cancellationToken).ConfigureAwait(false);
        await inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the send gate for one data frame, or reports that the frame must be dropped
    /// because a close has begun. The check is repeated under the gate: the first one only avoids
    /// queueing, and the second is the one a concurrent close is ordered against.</summary>
    private async ValueTask<bool> TryEnterSendAsync(CancellationToken cancellationToken)
    {
        if (_closing)
        {
            return false;
        }
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_closing)
        {
            _sendGate.Release();
            return false;
        }
        return true;
    }

    /// <summary>Marks the socket as closing, then waits for the data send that may be in flight.
    /// Once this returns no send is inside the wrapped socket and none can enter it, so the Close
    /// frame that follows is the last one written. The gate is released before the wrapped close
    /// runs: <see cref="CloseAsync"/> waits for the peer's reply, and holding the gate through that
    /// wait would block every other close.</summary>
    private async Task WaitForInFlightSendAsync(CancellationToken cancellationToken)
    {
        _closing = true;
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _sendGate.Release();
    }
}

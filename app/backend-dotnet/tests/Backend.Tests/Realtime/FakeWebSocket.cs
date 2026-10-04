using System.Net.WebSockets;

namespace Backend.Tests.Realtime;

/// <summary>Minimal fake <see cref="WebSocket"/> for <see cref="WebSocketFrameReaderTests"/> and
/// (issue #13 Wave 2) <c>AudioAppendFastPathTests</c>: replays a pre-configured sequence of
/// receive chunks (as if fed by <c>ReceiveAsync</c>), records every <c>SendAsync</c> call so a
/// test can assert on exactly what bytes were forwarded, and records any close call so tests can
/// assert on it without a real socket.</summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Queue<(byte[] Data, bool EndOfMessage, WebSocketMessageType MessageType)> _chunks;
    private readonly bool _hangCloseOutputUntilCancelled;
    private WebSocketState _state = WebSocketState.Open;

    public FakeWebSocket(
        IEnumerable<(byte[] Data, bool EndOfMessage, WebSocketMessageType MessageType)> chunks,
        bool hangCloseOutputUntilCancelled = false)
    {
        _chunks = new(chunks);
        _hangCloseOutputUntilCancelled = hangCloseOutputUntilCancelled;
    }

    public WebSocketCloseStatus? ClosedWithStatus { get; private set; }
    public string? ClosedWithDescription { get; private set; }
    public bool CloseOutputCalled { get; private set; }
    public bool CloseCalled { get; private set; }

    public override WebSocketCloseStatus? CloseStatus => ClosedWithStatus;
    public override string? CloseStatusDescription => ClosedWithDescription;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        CloseCalled = true;
        ClosedWithStatus = closeStatus;
        ClosedWithDescription = statusDescription;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        CloseOutputCalled = true;
        // Round-2 review of #244 (issue 1): simulates a non-draining peer whose outbound send
        // genuinely blocks -- the whole point of CloseSupersededStaleConnectionAsyncTests is to
        // prove the caller survives this deterministically (bounded by its own timeout token)
        // rather than hanging forever, the exact hazard Rick's probe found against a real
        // half-open transport. Task.Delay(Timeout.Infinite, ct) never completes on its own; it
        // only ever throws once cancellationToken fires, matching what a cancelled
        // WebSocket.CloseOutputAsync does against a stuck write.
        if (_hangCloseOutputUntilCancelled)
        {
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
        ClosedWithStatus = closeStatus;
        ClosedWithDescription = statusDescription;
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_chunks.Count == 0)
        {
            throw new InvalidOperationException("FakeWebSocket has no more chunks queued -- the reader kept reading past what the test expected.");
        }
        var (data, endOfMessage, messageType) = _chunks.Dequeue();
        data.CopyTo(buffer.Array!, buffer.Offset);
        return Task.FromResult(new WebSocketReceiveResult(data.Length, messageType, endOfMessage));
    }

    public List<(byte[] Data, WebSocketMessageType MessageType, bool EndOfMessage)> SentMessages { get; } = [];

    /// <summary>#244 round-2 re-review (CloseSupersededStaleConnectionAsync's settle-wait fix):
    /// simulates the real-world moment a healthy peer's own answering close frame is processed by
    /// the connection's already-pending <c>ReceiveAsync</c> -- i.e. the socket settling out of
    /// <see cref="WebSocketState.CloseSent"/> on its own, without anyone cancelling anything. Tests
    /// call this (typically from a short-lived background <c>Task.Run</c>) to prove the settle-wait
    /// notices promptly and does not simply wait out its full timeout budget for a peer that was
    /// never actually stuck.</summary>
    public void SimulatePeerAnsweredClose() => _state = WebSocketState.Closed;

    /// <summary>Issue #252 (Rick's review): an optional async hook invoked -- and fully awaited --
    /// from inside <see cref="SendAsync"/> before it returns, so a test can simulate "the upstream
    /// already processed a reply to THIS exact frame before the send call returned" (the precise
    /// interleaving a real race under scheduling pressure would produce) deterministically, with no
    /// real threads/timing involved. Unused by any other existing test (defaults to null, a no-op).</summary>
    public Func<byte[], Task>? OnSendAsync { get; set; }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        var bytes = buffer.ToArray();
        SentMessages.Add((bytes, messageType, endOfMessage));
        if (OnSendAsync is not null)
        {
            await OnSendAsync(bytes).ConfigureAwait(false);
        }
    }
}

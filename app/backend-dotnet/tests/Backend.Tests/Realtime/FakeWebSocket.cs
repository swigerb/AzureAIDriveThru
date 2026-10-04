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
    private WebSocketState _state = WebSocketState.Open;

    public FakeWebSocket(IEnumerable<(byte[] Data, bool EndOfMessage, WebSocketMessageType MessageType)> chunks)
    {
        _chunks = new(chunks);
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

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        SentMessages.Add((buffer.ToArray(), messageType, endOfMessage));
        return Task.CompletedTask;
    }
}

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Cascade;

namespace Backend.Realtime;

/// <summary>Observes bounded complete text frames without modifying transport or audio.</summary>
internal sealed class DiagnosticWebSocket(WebSocket inner, CascadeDiagnosticCapture capture, bool upstream) : WebSocket
{
    private readonly MemoryStream _sent = new();
    private readonly MemoryStream _received = new();
    private readonly Lock _sentLock = new();
    private readonly Lock _receivedLock = new();
    private bool _sentOverflow;
    private bool _receivedOverflow;
    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
    public override string? CloseStatusDescription => inner.CloseStatusDescription;
    public override WebSocketState State => inner.State;
    public override string? SubProtocol => inner.SubProtocol;
    public override void Abort() => inner.Abort();
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    public override void Dispose()
    {
        _sent.Dispose();
        _received.Dispose();
        // The caller retains ownership of the original socket.
    }
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
        if (upstream)
        {
            Observe(_sentLock, _sent, ref _sentOverflow, buffer.AsSpan(), messageType, endOfMessage, true);
        }
    }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        WebSocketReceiveResult result = await inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(_receivedLock, _received, ref _receivedOverflow, buffer.AsSpan(0, result.Count), result.MessageType, result.EndOfMessage, false);
        return result;
    }
    private void Observe(Lock gate, MemoryStream pending, ref bool overflow, ReadOnlySpan<byte> bytes, WebSocketMessageType type, bool end, bool outbound)
    {
        lock (gate)
        {
            ObserveLocked(pending, ref overflow, bytes, type, end, outbound);
        }
    }
    private void ObserveLocked(MemoryStream pending, ref bool overflow, ReadOnlySpan<byte> bytes, WebSocketMessageType type, bool end, bool outbound)
    {
        if (type != WebSocketMessageType.Text)
        {
            return;
        }

        if (!overflow && pending.Length + bytes.Length > 131072)
        {
            overflow = true;
            pending.SetLength(0);
            capture.StopForFrameLimit();
        }
        if (!overflow)
        {
            pending.Write(bytes);
        }

        if (!end)
        {
            return;
        }

        try
        {
            if (!overflow && JsonNode.Parse(Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length)) is JsonObject frame)
            {
                if (!upstream)
                {
                    if (frame["type"]?.GetValue<string>() == "extension.resume")
                    {
                        capture.Stop();
                    }
                }
                else
                {
                    capture.RealtimeFrame(frame, outbound);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            capture.Stop();
        }
        finally
        {
            pending.SetLength(0);
            overflow = false;
        }
    }
}

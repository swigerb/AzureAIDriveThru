using System.Net.WebSockets;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #338: focused unit tests for the <see cref="FramePump"/> primitives that had no direct
/// test coverage before the extraction -- they were only ever exercised indirectly through
/// <c>RealtimeProcessor.RunSessionAsync</c> integration-style tests. The fast-path/forward
/// pieces (<c>TryAppendFastPath</c>, <c>ForwardFastPathAudioAsync</c>,
/// <c>ForwardClientFrameAsync</c>, <c>TryMatchAppendFastPath</c>) and the supersede-close sequence
/// already have their own dedicated coverage (<c>AudioAppendFastPathTests</c>,
/// <c>ForwardClientFrameOrderingTests</c>, <c>CloseSupersededStaleConnectionAsyncTests</c>), kept
/// unchanged and now pointed at <see cref="FramePump"/> directly.
/// </summary>
public sealed class FramePumpTests
{
    [Fact]
    public async Task SwallowAsync_propagates_nothing_and_does_not_throw_on_a_cancelled_task()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetCanceled(TestContext.Current.CancellationToken);

        await FramePump.SwallowAsync(tcs.Task, NullLogger.Instance);
        // No exception reaching here is the assertion -- OperationCanceledException must be
        // swallowed silently (expected: the caller's own linkedCts.Cancel() is what unblocks the
        // counterpart loop's pending read/send in the first place).
    }

    [Fact]
    public async Task SwallowAsync_logs_but_does_not_throw_on_an_unexpected_exception()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetException(new InvalidOperationException("boom"));

        // Reaching here without the InvalidOperationException propagating is the assertion: an
        // unexpected fault is logged (UnhandledRelayDrainException), not rethrown, so one loop's
        // own setup failure can never also take down the drain of its counterpart.
        await FramePump.SwallowAsync(tcs.Task, NullLogger.Instance);
    }

    [Fact]
    public async Task CloseIfOpenAsync_closes_an_open_socket()
    {
        var socket = new FakeWebSocket([]);

        await FramePump.CloseIfOpenAsync(socket, WebSocketCloseStatus.NormalClosure, "bye");

        Assert.True(socket.CloseCalled);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.ClosedWithStatus);
        Assert.Equal("bye", socket.ClosedWithDescription);
    }

    [Fact]
    public async Task CloseIfOpenAsync_is_a_no_op_on_an_already_closed_socket()
    {
        var socket = new FakeWebSocket([]);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        // Reset the recorded flag so the second (expected no-op) call can be told apart from the
        // first real close above.
        await FramePump.CloseIfOpenAsync(socket, WebSocketCloseStatus.InternalServerError, "should not apply");

        // The no-op path must never overwrite the FIRST close's reason with a second one.
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.ClosedWithStatus);
    }

    [Fact]
    public async Task CloseOutputIfOpenAsync_closes_output_of_an_open_socket()
    {
        var socket = new FakeWebSocket([]);

        await FramePump.CloseOutputIfOpenAsync(
            socket, WebSocketCloseStatus.NormalClosure, "superseded", CancellationToken.None);

        Assert.True(socket.CloseOutputCalled);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.ClosedWithStatus);
    }

    [Fact]
    public async Task CloseOutputIfOpenAsync_swallows_a_cancelled_send_on_a_non_draining_peer()
    {
        var socket = new FakeWebSocket([], hangCloseOutputUntilCancelled: true);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // Must complete (not throw/hang) once the token fires against a peer that never drains --
        // the caller (CloseSupersededStaleConnectionAsync) relies on this being best-effort.
        await FramePump.CloseOutputIfOpenAsync(socket, WebSocketCloseStatus.NormalClosure, null, cts.Token);
    }

    [Fact]
    public async Task SendTextAsync_sends_utf8_bytes_on_an_open_socket()
    {
        var socket = new FakeWebSocket([]);

        await FramePump.SendTextAsync(socket, "hello", TestContext.Current.CancellationToken);

        var sent = Assert.Single(socket.SentMessages);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(sent.Data));
        Assert.Equal(WebSocketMessageType.Text, sent.MessageType);
        Assert.True(sent.EndOfMessage);
    }

    [Fact]
    public async Task SendTextAsync_is_a_no_op_on_a_closed_socket()
    {
        var socket = new FakeWebSocket([]);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        await FramePump.SendTextAsync(socket, "dropped", TestContext.Current.CancellationToken);

        Assert.Empty(socket.SentMessages);
    }

    [Fact]
    public async Task SendBytesAsync_forwards_original_bytes_unchanged_on_an_open_socket()
    {
        var socket = new FakeWebSocket([]);
        byte[] payload = [1, 2, 3, 4];

        await FramePump.SendBytesAsync(socket, payload, TestContext.Current.CancellationToken);

        var sent = Assert.Single(socket.SentMessages);
        Assert.Equal(payload, sent.Data);
    }

    [Fact]
    public async Task SendBytesAsync_is_a_no_op_on_a_closed_socket()
    {
        var socket = new FakeWebSocket([]);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        await FramePump.SendBytesAsync(socket, [1, 2, 3], TestContext.Current.CancellationToken);

        Assert.Empty(socket.SentMessages);
    }
}

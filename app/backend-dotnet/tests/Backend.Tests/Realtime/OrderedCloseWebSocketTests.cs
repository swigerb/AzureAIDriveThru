using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>
/// <see cref="OrderedCloseWebSocket"/> keeps the Close frame the last frame written to the browser.
/// The ordering cases run against a probe socket whose in-flight send can be held open, so each one
/// pins down one interleaving without any timing. The last two cases wrap a real
/// <c>ManagedWebSocket</c> and parse the bytes it writes, which is the only place the actual wire
/// order shows.
/// </summary>
public sealed class OrderedCloseWebSocketTests
{
    private const byte TextOpcode = 0x1;
    private const byte CloseOpcode = 0x8;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static ArraySegment<byte> Frame(string text) => new(Bytes(text));

    /// <summary>Bounds an await so a regression fails the test instead of hanging the run.</summary>
    private static Task Within(Task task) => task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Sends_pass_through_in_order_while_nothing_is_closing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);

        await Within(socket.SendAsync(Frame("A"), WebSocketMessageType.Text, true, ct));
        await Within(socket.SendAsync(Frame("B"), WebSocketMessageType.Text, true, ct));

        Assert.Equal(["send-start:A", "send-end:A", "send-start:B", "send-end:B"], inner.Events);
    }

    [Fact]
    public async Task A_close_requested_during_a_send_waits_for_that_send_and_goes_out_after_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);
        TaskCompletionSource release = inner.HoldSend("S1");

        Task inFlight = socket.SendAsync(Frame("S1"), WebSocketMessageType.Text, true, ct);
        await Within(inner.SendStarted("S1"));
        Task close = socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);

        Assert.False(close.IsCompleted);
        Assert.Equal(["send-start:S1"], inner.Events);

        release.SetResult();
        await Within(inFlight);
        await Within(close);

        Assert.Equal(["send-start:S1", "send-end:S1", "close-output:NormalClosure:bye"], inner.Events);
    }

    [Fact]
    public async Task A_send_issued_after_a_close_was_requested_is_dropped_without_error()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);
        TaskCompletionSource release = inner.HoldSend("S1");

        Task inFlight = socket.SendAsync(Frame("S1"), WebSocketMessageType.Text, true, ct);
        await Within(inner.SendStarted("S1"));
        Task close = socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
        Task late = socket.SendAsync(Frame("S2"), WebSocketMessageType.Text, true, ct);

        Assert.True(late.IsCompletedSuccessfully);

        release.SetResult();
        await Within(inFlight);
        await Within(close);
        await Within(socket.SendAsync(Frame("S3"), WebSocketMessageType.Text, true, ct));

        Assert.Equal(["send-start:S1", "send-end:S1", "close-output:NormalClosure:bye"], inner.Events);
    }

    [Fact]
    public async Task A_send_already_queued_when_a_close_is_requested_is_dropped_not_written_after_the_close()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);
        TaskCompletionSource release = inner.HoldSend("S1");

        Task inFlight = socket.SendAsync(Frame("S1"), WebSocketMessageType.Text, true, ct);
        await Within(inner.SendStarted("S1"));
        Task queued = socket.SendAsync(Frame("S2"), WebSocketMessageType.Text, true, ct);
        Assert.False(queued.IsCompleted);
        Task close = socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);

        release.SetResult();
        await Within(inFlight);
        await Within(queued);
        await Within(close);

        Assert.Equal(["send-start:S1", "send-end:S1", "close-output:NormalClosure:bye"], inner.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_close_is_delegated_once_with_the_callers_arguments(bool fullHandshake)
    {
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);
        using var cts = new CancellationTokenSource();

        await Within(fullHandshake
            ? socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "too chatty", cts.Token)
            : socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "too chatty", cts.Token));

        ProbeWebSocket.CloseCall call = Assert.Single(inner.Closes);
        Assert.Equal(fullHandshake ? "close" : "close-output", call.Method);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, call.Status);
        Assert.Equal("too chatty", call.Description);
        Assert.Equal(cts.Token, call.Token);
    }

    [Fact]
    public async Task A_close_still_waiting_for_the_peer_does_not_block_another_close()
    {
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);
        TaskCompletionSource peerReply = inner.HoldCloseHandshake();

        Task handshake = socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", TestContext.Current.CancellationToken);
        await Within(inner.CloseHandshakeStarted);
        Task output = socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "again", TestContext.Current.CancellationToken);

        await Within(output);
        Assert.False(handshake.IsCompleted);

        peerReply.SetResult();
        await Within(handshake);

        Assert.Equal(["close:NormalClosure:bye", "close-output:EndpointUnavailable:again"], inner.Events);
    }

    [Fact]
    public async Task A_send_cancelled_while_queued_throws_without_reaching_the_socket_or_wedging_the_gate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);
        TaskCompletionSource release = inner.HoldSend("S1");
        using var cts = new CancellationTokenSource();

        Task inFlight = socket.SendAsync(Frame("S1"), WebSocketMessageType.Text, true, ct);
        await Within(inner.SendStarted("S1"));
        Task cancelled = socket.SendAsync(Frame("S2"), WebSocketMessageType.Text, true, cts.Token);
        Assert.False(cancelled.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        release.SetResult();
        await Within(inFlight);
        await Within(socket.SendAsync(Frame("S3"), WebSocketMessageType.Text, true, ct));

        Assert.Equal(["send-start:S1", "send-end:S1", "send-start:S3", "send-end:S3"], inner.Events);
    }

    [Fact]
    public async Task A_close_cancelled_while_waiting_leaves_the_socket_closing_and_a_later_close_still_goes_out()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);
        TaskCompletionSource release = inner.HoldSend("S1");
        using var cts = new CancellationTokenSource();

        Task inFlight = socket.SendAsync(Frame("S1"), WebSocketMessageType.Text, true, ct);
        await Within(inner.SendStarted("S1"));
        Task bounded = socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bounded);

        Assert.Equal(["send-start:S1"], inner.Events);
        await Within(socket.SendAsync(Frame("S2"), WebSocketMessageType.Text, true, ct));

        release.SetResult();
        await Within(inFlight);
        await Within(socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct));

        Assert.Equal(["send-start:S1", "send-end:S1", "close-output:NormalClosure:bye"], inner.Events);
    }

    [Fact]
    public async Task The_memory_overloads_are_ordered_against_a_close_like_the_segment_overload()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);

        await socket.SendAsync(Bytes("M0").AsMemory(), WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, ct);
        TaskCompletionSource release = inner.HoldSend("M1");
        Task inFlight = socket.SendAsync(Bytes("M1").AsMemory(), WebSocketMessageType.Text, true, ct).AsTask();
        await Within(inner.SendStarted("M1"));
        Task close = socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);

        await socket.SendAsync(Bytes("M2").AsMemory(), WebSocketMessageType.Text, true, ct);
        await socket.SendAsync(Bytes("M3").AsMemory(), WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, ct);
        Assert.False(close.IsCompleted);

        release.SetResult();
        await Within(inFlight);
        await Within(close);

        Assert.Equal(
            ["send-start:M0", "send-end:M0", "send-start:M1", "send-end:M1", "close-output:NormalClosure:"],
            inner.Events);
    }

    [Fact]
    public void State_the_close_properties_and_Abort_pass_straight_through()
    {
        var inner = new ProbeWebSocket
        {
            ReportedState = WebSocketState.CloseReceived,
            ReportedCloseStatus = WebSocketCloseStatus.EndpointUnavailable,
            ReportedCloseDescription = "going away",
            ReportedSubProtocol = "drivethru.v1",
        };
        using var socket = new OrderedCloseWebSocket(inner);

        Assert.Equal(WebSocketState.CloseReceived, socket.State);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
        Assert.Equal("going away", socket.CloseStatusDescription);
        Assert.Equal("drivethru.v1", socket.SubProtocol);

        socket.Abort();

        Assert.Equal(1, inner.AbortCount);
    }

    [Fact]
    public async Task Receives_are_never_blocked_by_a_send_that_is_stuck()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var inner = new ProbeWebSocket();
        using var socket = new OrderedCloseWebSocket(inner);
        TaskCompletionSource release = inner.HoldSend("S1");

        Task inFlight = socket.SendAsync(Frame("S1"), WebSocketMessageType.Text, true, ct);
        await Within(inner.SendStarted("S1"));

        WebSocketReceiveResult segment = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[8]), ct).WaitAsync(s_timeout, ct);
        ValueWebSocketReceiveResult memory = await socket.ReceiveAsync(new byte[8].AsMemory(), ct).AsTask().WaitAsync(s_timeout, ct);

        Assert.Equal(2, segment.Count);
        Assert.Equal(WebSocketMessageType.Text, segment.MessageType);
        Assert.Equal(2, memory.Count);
        Assert.True(memory.EndOfMessage);

        release.SetResult();
        await Within(inFlight);
    }

    [Fact]
    public void Dispose_leaves_the_wrapped_socket_to_its_owner()
    {
        var inner = new ProbeWebSocket();
        var socket = new OrderedCloseWebSocket(inner);

        socket.Dispose();

        Assert.Equal(0, inner.DisposeCount);
    }

    [Fact]
    public async Task No_data_frame_starts_after_the_close_or_is_still_being_written_when_it_goes_out()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int Rounds = 300;
        const int Senders = 6;
        const int SendsPerSender = 25;

        for (int round = 0; round < Rounds; round++)
        {
            var inner = new ProbeWebSocket();
            using var socket = new OrderedCloseWebSocket(inner);
            int yieldsBeforeClose = Random.Shared.Next(0, 60);
            List<Task> work = [];
            for (int sender = 0; sender < Senders; sender++)
            {
                int id = sender;
                work.Add(Task.Run(async () =>
                {
                    for (int n = 0; n < SendsPerSender; n++)
                    {
                        string text = id.ToString(CultureInfo.InvariantCulture) + ":" + n.ToString(CultureInfo.InvariantCulture);
                        await socket.SendAsync(Frame(text), WebSocketMessageType.Text, true, ct);
                    }
                }, ct));
            }
            work.Add(Task.Run(async () =>
            {
                for (int i = 0; i < yieldsBeforeClose; i++)
                {
                    await Task.Yield();
                }
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            }, ct));

            await Task.WhenAll(work).WaitAsync(s_timeout, ct);

            AssertCloseIsLast(inner.Events);
        }
    }

    [Fact]
    public async Task On_a_real_socket_a_send_that_races_a_close_cannot_land_after_the_Close_frame()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var wire = new BlockFirstWriteStream();
        using var real = WebSocket.CreateFromStream(wire, isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        using var socket = new OrderedCloseWebSocket(real);

        Task first = socket.SendAsync(Frame("S1"), WebSocketMessageType.Text, true, ct);
        await wire.FirstWriteStarted.WaitAsync(s_timeout, ct);
        Task close = socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
        Task late = socket.SendAsync(Frame("S2"), WebSocketMessageType.Text, true, ct);
        Assert.True(late.IsCompletedSuccessfully);

        wire.ReleaseFirstWrite();
        await Task.WhenAll(first, close, late).WaitAsync(s_timeout, ct);

        List<(byte Opcode, byte[] Payload)> frames = ParseServerFrames(wire.Written);
        Assert.Equal([TextOpcode, CloseOpcode], frames.Select(f => f.Opcode));
        Assert.Equal("S1", Encoding.UTF8.GetString(frames[0].Payload));
        AssertCloseFrame(frames[1].Payload, WebSocketCloseStatus.NormalClosure, "bye");
        Assert.Equal(WebSocketState.CloseSent, real.State);
    }

    [Fact]
    public async Task On_a_real_socket_a_full_close_handshake_also_follows_the_send_in_flight()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // The browser's answering Close frame: masked (client frames always are), 2-byte payload
        // 0x03E8 = 1000, all-zero mask key.
        var wire = new BlockFirstWriteStream(peerBytes: [0x88, 0x82, 0x00, 0x00, 0x00, 0x00, 0x03, 0xE8]);
        using var real = WebSocket.CreateFromStream(wire, isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        using var socket = new OrderedCloseWebSocket(real);

        Task first = socket.SendAsync(Frame("S1"), WebSocketMessageType.Text, true, ct);
        await wire.FirstWriteStarted.WaitAsync(s_timeout, ct);
        Task close = socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
        Task late = socket.SendAsync(Frame("S2"), WebSocketMessageType.Text, true, ct);
        Assert.True(late.IsCompletedSuccessfully);

        wire.ReleaseFirstWrite();
        await Task.WhenAll(first, close, late).WaitAsync(s_timeout, ct);

        List<(byte Opcode, byte[] Payload)> frames = ParseServerFrames(wire.Written);
        Assert.Equal([TextOpcode, CloseOpcode], frames.Select(f => f.Opcode));
        Assert.Equal("S1", Encoding.UTF8.GetString(frames[0].Payload));
        AssertCloseFrame(frames[1].Payload, WebSocketCloseStatus.NormalClosure, "bye");
        Assert.Equal(WebSocketState.Closed, real.State);
    }

    private static void AssertCloseIsLast(IReadOnlyList<string> events)
    {
        int inFlight = 0;
        bool closed = false;
        foreach (string recorded in events)
        {
            if (recorded.StartsWith("send-start:", StringComparison.Ordinal))
            {
                Assert.False(closed, "a data frame started after the Close frame: " + string.Join(" | ", events));
                inFlight++;
            }
            else if (recorded.StartsWith("send-end:", StringComparison.Ordinal))
            {
                inFlight--;
            }
            else if (recorded.StartsWith("close-output:", StringComparison.Ordinal))
            {
                Assert.True(inFlight == 0, "a data frame was still being written when the Close frame went out: " + string.Join(" | ", events));
                closed = true;
            }
        }
        Assert.True(closed, "the close never reached the socket");
    }

    /// <summary>Splits the bytes a server-side socket wrote into frames. Server frames are never
    /// masked, and everything these tests send is shorter than 126 bytes, so the length is the
    /// low seven bits of the second header byte.</summary>
    private static List<(byte Opcode, byte[] Payload)> ParseServerFrames(byte[] written)
    {
        List<(byte Opcode, byte[] Payload)> frames = [];
        int index = 0;
        while (index < written.Length)
        {
            byte opcode = (byte)(written[index] & 0x0F);
            Assert.Equal(0, written[index + 1] & 0x80);
            int length = written[index + 1] & 0x7F;
            Assert.True(length < 126);
            frames.Add((opcode, written[(index + 2)..(index + 2 + length)]));
            index += 2 + length;
        }
        return frames;
    }

    private static void AssertCloseFrame(byte[] payload, WebSocketCloseStatus expectedStatus, string expectedReason)
    {
        Assert.Equal((int)expectedStatus, (payload[0] << 8) | payload[1]);
        Assert.Equal(expectedReason, Encoding.UTF8.GetString(payload, 2, payload.Length - 2));
    }

    /// <summary>Stands in for the socket the framework accepted. Records every call that reaches it,
    /// in order, so a test can assert what the decorator let through and when. One send, or the
    /// close handshake, can be held open so the test decides which operation is in flight.</summary>
    private sealed class ProbeWebSocket : WebSocket
    {
        private readonly Lock _sync = new();
        private readonly List<string> _events = [];
        private readonly Dictionary<string, TaskCompletionSource> _sendHolds = [with(StringComparer.Ordinal)];
        private readonly Dictionary<string, TaskCompletionSource> _sendStarted = [with(StringComparer.Ordinal)];
        private readonly TaskCompletionSource _closeHandshakeStarted = NewSignal();
        private TaskCompletionSource? _closeHandshakeHold;

        internal readonly record struct CloseCall(string Method, WebSocketCloseStatus Status, string? Description, CancellationToken Token);

        public WebSocketState ReportedState { get; set; } = WebSocketState.Open;
        public WebSocketCloseStatus? ReportedCloseStatus { get; set; }
        public string? ReportedCloseDescription { get; set; }
        public string? ReportedSubProtocol { get; set; }
        public int AbortCount { get; private set; }
        public int DisposeCount { get; private set; }

        public List<CloseCall> Closes { get; } = [];

        public Task CloseHandshakeStarted => _closeHandshakeStarted.Task;

        public string[] Events
        {
            get
            {
                lock (_sync)
                {
                    return [.. _events];
                }
            }
        }

        /// <summary>The next send of <paramref name="text"/> stays in flight until the returned
        /// source is completed.</summary>
        public TaskCompletionSource HoldSend(string text)
        {
            TaskCompletionSource hold = NewSignal();
            lock (_sync)
            {
                _sendHolds[text] = hold;
            }
            return hold;
        }

        public Task SendStarted(string text)
        {
            lock (_sync)
            {
                if (!_sendStarted.TryGetValue(text, out TaskCompletionSource? started))
                {
                    started = NewSignal();
                    _sendStarted[text] = started;
                }
                return started.Task;
            }
        }

        /// <summary>The next <see cref="CloseAsync"/> stays in the peer handshake until the returned
        /// source is completed.</summary>
        public TaskCompletionSource HoldCloseHandshake()
        {
            TaskCompletionSource hold = NewSignal();
            _closeHandshakeHold = hold;
            return hold;
        }

        public override WebSocketCloseStatus? CloseStatus => ReportedCloseStatus;
        public override string? CloseStatusDescription => ReportedCloseDescription;
        public override WebSocketState State => ReportedState;
        public override string? SubProtocol => ReportedSubProtocol;

        public override void Abort() => AbortCount++;

        public override void Dispose() => DisposeCount++;

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            RecordClose("close", closeStatus, statusDescription, cancellationToken);
            _closeHandshakeStarted.TrySetResult();
            if (_closeHandshakeHold is { } hold)
            {
                await hold.Task.ConfigureAwait(false);
            }
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            RecordClose("close-output", closeStatus, statusDescription, cancellationToken);
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            Bytes("hi").CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(2, WebSocketMessageType.Text, true));
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            string text = Encoding.UTF8.GetString(buffer.AsSpan());
            TaskCompletionSource? hold;
            TaskCompletionSource started;
            lock (_sync)
            {
                _events.Add("send-start:" + text);
                _sendHolds.Remove(text, out hold);
                if (!_sendStarted.TryGetValue(text, out TaskCompletionSource? existing))
                {
                    existing = NewSignal();
                    _sendStarted[text] = existing;
                }
                started = existing;
            }
            started.TrySetResult();

            if (hold is null)
            {
                await Task.Yield();
            }
            else
            {
                await hold.Task.ConfigureAwait(false);
            }

            lock (_sync)
            {
                _events.Add("send-end:" + text);
            }
        }

        private void RecordClose(string method, WebSocketCloseStatus status, string? description, CancellationToken token)
        {
            lock (_sync)
            {
                _events.Add(method + ":" + status + ":" + description);
                Closes.Add(new CloseCall(method, status, description, token));
            }
        }
    }

    /// <summary>The bottom of a real <c>ManagedWebSocket</c>. Its first write blocks until the test
    /// releases it, which holds the socket's own send lock the way a slow peer does. Every byte
    /// written is kept in order, and <paramref name="peerBytes"/> is what the socket reads back.</summary>
    private sealed class BlockFirstWriteStream(byte[]? peerBytes = null) : Stream
    {
        private readonly Lock _sync = new();
        private readonly MemoryStream _written = new();
        private readonly TaskCompletionSource _firstWriteStarted = NewSignal();
        private readonly TaskCompletionSource _firstWriteReleased = NewSignal();
        private bool _firstWriteSeen;
        private int _readOffset;

        public Task FirstWriteStarted => _firstWriteStarted.Task;

        public byte[] Written
        {
            get
            {
                lock (_sync)
                {
                    return _written.ToArray();
                }
            }
        }

        public void ReleaseFirstWrite() => _firstWriteReleased.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            bool first;
            lock (_sync)
            {
                _written.Write(buffer.Span);
                first = !_firstWriteSeen;
                _firstWriteSeen = true;
            }
            if (first)
            {
                _firstWriteStarted.TrySetResult();
                await _firstWriteReleased.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (peerBytes is not null && _readOffset < peerBytes.Length)
            {
                int count = Math.Min(buffer.Length, peerBytes.Length - _readOffset);
                peerBytes.AsMemory(_readOffset, count).CopyTo(buffer);
                _readOffset += count;
                return count;
            }
            // An idle peer: nothing else arrives until the socket is torn down.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _written.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

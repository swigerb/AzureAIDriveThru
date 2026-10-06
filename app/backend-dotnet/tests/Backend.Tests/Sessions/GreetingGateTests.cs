using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #338: focused unit tests for <see cref="GreetingGate"/>, extracted from
/// <see cref="RealtimeProcessor.RunSessionAsync"/>'s own <c>SendGreetingOnceAsync</c> local
/// closure. These prove the same behaviour the extraction promises to preserve: send exactly
/// once, wait (bounded) for session.updated first, and fall back to the generic greeting when no
/// persona prompt pack is bound.
/// </summary>
public sealed class GreetingGateTests
{
    private static RealtimeProcessor.RealtimeSessionState NewState(string sessionId) => new()
    {
        SessionId = sessionId,
        Voice = "marin",
        Echo = new EchoSuppressor(0, _ => Task.CompletedTask),
        RateLimit = new RateLimitRecovery(
            new RateLimitSettings(),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            NullLogger<RateLimitRecovery>.Instance),
        ToolFailures = new ToolFailureTracker(),
        Guard = new SessionUpdateGuard(),
        Identifiers = new SessionIdentifiers("sonic", "gpt-realtime"),
        ToolExecutor = new StubToolExecutor([]),
    };

    [Fact]
    public async Task SendOnceAsync_sends_the_fallback_greeting_when_session_configured_completes_immediately()
    {
        var state = NewState("session-1");
        state.SessionConfigured.TrySetResult(true);
        var upstream = new FakeWebSocket([]);

        var gate = new GreetingGate(
            upstream, promptLoader: null, state, sessionManager: null, TimeProvider.System,
            greetingTimeoutSeconds: 5, sessionId: "session-1", NullLogger.Instance, CancellationToken.None);

        await gate.SendOnceAsync("greeting-timer");

        Assert.True(state.GreetingSent);
        // input_audio_buffer.clear, the greeting item, then response.create -- in that order.
        Assert.Equal(3, upstream.SentMessages.Count);
        Assert.Contains("input_audio_buffer.clear", System.Text.Encoding.UTF8.GetString(upstream.SentMessages[0].Data));
        var greetingFrame = JsonNode.Parse(upstream.SentMessages[1].Data)!.AsObject();
        Assert.Equal("conversation.item.create", (string?)greetingFrame["type"]);
        Assert.Equal("Hello!", (string?)greetingFrame["item"]!["content"]![0]!["text"]);
        Assert.Contains("response.create", System.Text.Encoding.UTF8.GetString(upstream.SentMessages[2].Data));
    }

    [Fact]
    public async Task SendOnceAsync_is_idempotent()
    {
        var state = NewState("session-2");
        state.SessionConfigured.TrySetResult(true);
        var upstream = new FakeWebSocket([]);
        var gate = new GreetingGate(
            upstream, promptLoader: null, state, sessionManager: null, TimeProvider.System,
            greetingTimeoutSeconds: 5, sessionId: "session-2", NullLogger.Instance, CancellationToken.None);

        await gate.SendOnceAsync("first-trigger");
        await gate.SendOnceAsync("second-trigger");

        // The second call must be a complete no-op -- no extra frames sent upstream.
        Assert.Equal(3, upstream.SentMessages.Count);
    }

    [Fact]
    public async Task SendOnceAsync_does_not_resend_if_GreetingSent_was_already_set_externally()
    {
        // Mirrors RunSessionAsync's resume path, which sets state.GreetingSent directly
        // (outcome.ConversationStarted) without ever calling SendOnceAsync.
        var state = NewState("session-3");
        state.GreetingSent = true;
        var upstream = new FakeWebSocket([]);
        var gate = new GreetingGate(
            upstream, promptLoader: null, state, sessionManager: null, TimeProvider.System,
            greetingTimeoutSeconds: 5, sessionId: "session-3", NullLogger.Instance, CancellationToken.None);

        await gate.SendOnceAsync("client-session.update");

        Assert.Empty(upstream.SentMessages);
    }

    [Fact]
    public async Task SendOnceAsync_sends_anyway_after_the_session_configured_wait_times_out()
    {
        var state = NewState("session-4");
        // Never resolved -- forces the WaitAsync timeout branch.
        var upstream = new FakeWebSocket([]);
        var gate = new GreetingGate(
            upstream, promptLoader: null, state, sessionManager: null, TimeProvider.System,
            greetingTimeoutSeconds: 0.05, sessionId: "session-4", NullLogger.Instance, CancellationToken.None);

        await gate.SendOnceAsync("timeout-fallback");

        Assert.True(state.GreetingSent);
        Assert.Equal(3, upstream.SentMessages.Count);
    }
}

using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Prompts;
using Backend.Realtime;
using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Issue #338: the greeting-gate collaborator extracted from
/// <see cref="RealtimeProcessor.RunSessionAsync"/> -- rtmt.py's own <c>send_greeting_once</c>
/// local closure, ported verbatim. Waits for the upstream session.updated acknowledgement (or a
/// timeout) before sending the first greeting, exactly once per connection, and is also
/// consulted (via <see cref="RealtimeProcessor.RealtimeSessionState.GreetingSent"/>) by a
/// successful resume that already knows the conversation started elsewhere.
///
/// Pure move-and-delegate: every line below is unchanged from the original local functions
/// (<c>ParseGreetingTimeoutSeconds</c>, <c>BuildGreetingFrame</c>, <c>SendGreetingOnceAsync</c>)
/// except that closed-over locals became constructor parameters and <c>state.GreetingSent</c>
/// became <see cref="RealtimeProcessor.RealtimeSessionState.GreetingSent"/> on the same shared
/// <paramref name="state"/> instance (still the single source of truth both this gate and
/// <c>RunSessionAsync</c>'s resume path read/write).
/// </summary>
internal sealed class GreetingGate(
    WebSocket upstream,
    PromptLoader? promptLoader,
    RealtimeProcessor.RealtimeSessionState state,
    SessionManager? sessionManager,
    TimeProvider timeProvider,
    double greetingTimeoutSeconds,
    string sessionId,
    ILogger logger,
    CancellationToken ct)
{
    private double ParseGreetingTimeoutSeconds()
    {
        var raw = BackendEnvironment.Get("CONFORMANCE_GREETING_TIMEOUT_SECONDS");
        return double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : greetingTimeoutSeconds;
    }

    private JsonObject BuildGreetingFrame()
    {
        JsonObject greeting = promptLoader is not null
            ? (JsonObject)YamlJson.ToJsonNode((IDictionary<object, object>)promptLoader.Greeting)!
            : new JsonObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Hello!" }),
                },
            };
        if (greeting["item"] is JsonObject item)
        {
            item["id"] = MiddleTierItemIds.NewId();
        }
        return greeting;
    }

    public async Task SendOnceAsync(string trigger)
    {
        if (state.GreetingSent)
        {
            return;
        }
        var timeoutSeconds = ParseGreetingTimeoutSeconds();
        try
        {
            await state.SessionConfigured.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), timeProvider, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger?.NoSessionUpdatedBeforeGreeting(timeoutSeconds, sessionId);
        }
        if (state.GreetingSent)
        {
            return;
        }
        state.GreetingSent = true;
        state.Echo.StartGreetingSuppression();
        logger?.SendingGreeting(trigger, sessionId);
        var greetingFrameJson = BuildGreetingFrame().ToJsonString();
        await FramePump.SendTextAsync(upstream, """{"type":"input_audio_buffer.clear"}""", ct).ConfigureAwait(false);
        await FramePump.SendTextAsync(upstream, greetingFrameJson, ct).ConfigureAwait(false);
        await FramePump.SendTextAsync(upstream, """{"type":"response.create"}""", ct).ConfigureAwait(false);
        // Issue #13 tail: track the greeting in the context window, mirroring rtmt.py's
        // ctx_monitor.add_content(greeting_msg) right after the greeting is sent.
        sessionManager?.GetContextMonitor(state.EffectiveSessionId)?.AddContent(greetingFrameJson);
        // Rick's #244 review (issue 5): rtmt.py's send_greeting_once marks
        // conversation_started immediately after sending the greeting's response.create
        // (mark_greeting_sent), NOT after the greeting's response.done later arrives -- a
        // connection drop between those two points must still resume silently (rehydrating,
        // no re-greet), not fall back to greeting again, since the guest already heard it
        // start. Previously this port only marked it at the first non-tool-call response.done,
        // which is wrong specifically for that drop-during-the-greeting window.
        sessionManager?.MarkConversationStarted(state.EffectiveSessionId);
    }
}

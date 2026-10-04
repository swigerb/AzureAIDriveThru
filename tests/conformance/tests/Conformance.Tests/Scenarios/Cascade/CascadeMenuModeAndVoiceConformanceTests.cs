using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// #248: the Python-cascade parity gaps fixed in <c>cascade_processor.py</c> (per-persona default
/// voice, `?mode=` daypart binding, and -- PR #253 review item 1 -- `extension.set_voice`
/// sanitization), proven end to end over the real wire protocol against
/// <see cref="CascadeMenuModeAndVoiceConformanceFixture"/>'s <c>test-delta</c> pack (same pack
/// <c>MenuModeConformanceTests</c> already proved out for the realtime pipeline).
///
/// Left UNTAGGED (no <c>[Trait("Dotnet", "ready")]</c>): PR #236 (the C# cascade pipeline port,
/// issue #13 Wave 5) was not merged as of this PR, so `app/backend-dotnet` has no
/// `CascadeProcessor.cs` to run these rows against yet. Tag these `Dotnet="ready"` once #236
/// merges AND the dotnet cascade pipeline implements the same per-persona voice/mode binding AND
/// `extension.set_voice` sanitization this PR ports on the Python side (#236's own PR description
/// already shows the C# cascade has the first two -- this is a parity port onto the OTHER leg, not
/// new C# work; Rick flagged in PR #253 review that #236's own `CascadeProcessor.cs` ~565-570 has
/// the SAME unsanitized `extension.set_voice` gap as the pre-fix Python code here, so #236 needs
/// its own fix before any of these rows can be tagged ready).
/// </summary>
[Collection(CascadeMenuModeAndVoiceConformanceCollection.Name)]
public sealed class CascadeMenuModeAndVoiceConformanceTests(CascadeMenuModeAndVoiceConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = CascadeScenarioHelpers.FrameTimeout;
    private const string Persona = CascadeMenuModeAndVoiceConformanceFixture.Persona_;

    private static JsonObject ToolCallMessage(string toolCallId, string toolName, string argumentsJson) => new()
    {
        ["role"] = "assistant",
        ["content"] = null,
        ["tool_calls"] = new JsonArray(new JsonObject
        {
            ["id"] = toolCallId,
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = toolName, ["arguments"] = argumentsJson },
        }),
    };

    private static JsonObject FinalMessage(string content) => new() { ["role"] = "assistant", ["content"] = content };

    /// <summary>
    /// Mutation-test seam for `_resolve_persona_voice` actually being wired into
    /// `CascadeProcessor._run_session` (not just correct in isolation -- see that function's own
    /// unit-test doc comment on the Python side): reverting `_run_session`'s
    /// `voice = _resolve_persona_voice(...)` back to the old hard-coded `voice=self.default_voice`
    /// makes this assertion fail -- the greeting would reach TTS with "marin" (the harness's
    /// deployment-wide default, `BackendContract.DefaultVoice`), not test-delta's own "alloy".
    /// </summary>
    [Fact]
    public Task Cascade_greeting_is_spoken_with_the_bound_personas_own_voice() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.Chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = "Welcome!" });
        var browser = await CascadeScenarioHelpers.ConnectAsync(fixture, "gpt-5-mini", ct, persona: Persona);
        await using var _ = browser;

        var audioDelta = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(audioDelta is not null, "Expected at least one response.audio.delta chunk for the greeting.");

        Assert.Contains("alloy", fixture.Realtime.TtsRequestVoices);
        Assert.DoesNotContain("marin", fixture.Realtime.TtsRequestVoices);
    });

    /// <summary>
    /// Mutation-test seam for the `?mode=` binding into `create_session` (`CascadeProcessor.handle`):
    /// reverting `handle()`'s `menu_mode=requested_menu_mode` pass-through back to the old
    /// no-`menu_mode`-argument call does NOT leave the session unfiltered -- `order_state
    /// .OrderState.create_session` defaults an unbound/invalid mode to `"lunch"` for any
    /// `features.dayparts` persona (see that method's own comment) -- so the session silently
    /// binds to "lunch" instead of the requested "breakfast", and this breakfast-only item is
    /// then rejected as `item_out_of_mode` (the `update_order` call never reaches the client at
    /// all, since tools.py's rejection path is `ToolResultDirection.TO_SERVER`). Confirmed: this
    /// exact mutation makes this row fail with a timeout waiting for
    /// `extension.middle_tier_tool_response(update_order)`, since the add is rejected and nothing
    /// is ever sent to the client. This is the real proof `?mode=` is wired through; the sibling
    /// lunch-mode REJECTION row below is NOT mutation-sensitive to this same change (its own doc
    /// comment explains why) and instead pins the rejection wire shape.
    /// </summary>
    [Fact]
    public Task Cascade_session_bound_to_breakfast_mode_accepts_the_breakfast_only_meal() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, persona: Persona, mode: "breakfast");
        await using var browser = connection.Browser;

        fixture.Chat.EnqueueMessage(ToolCallMessage(
            "call_add_breakfast", "update_order",
            """{"action":"add","item_name":"Delta Breakfast Meal","size":"regular","quantity":1}"""));
        fixture.Chat.EnqueueMessage(FinalMessage("Added your breakfast meal."));
        fixture.Realtime.NextTranscript = "I'd like a Delta Breakfast Meal.";

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var toolResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var name) && name.GetString() == "update_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null, $"Expected an extension.middle_tier_tool_response(update_order) within {FrameTimeout}.");

        var order = JsonDocument.Parse(toolResponse!.Json.GetProperty("tool_result").GetString()!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());

        // Rick's PR #253 review item 2: drain to this turn's OWN response.done (round 2's
        // scripted "Added your breakfast meal." final message, plus its TTS) before disposing.
        // The real reason this matters is FakeChatCompletionsServer's scripted-response FIFO, not
        // connection state: if the WS were disposed while round 2's /chat/completions request is
        // still in flight, the client-side cancellation can abort that request server-side WHILE
        // HandleCompletionAsync is still parsing its body -- which happens BEFORE the FIFO dequeue
        // -- leaving round 2's own scripted message stuck in the queue to be wrongly handed out to
        // whichever request (this scenario's or an unrelated later one's) arrives next. The
        // drain's result is asserted non-null below: a timed-out drain (the turn never reaching
        // its own response.done at all) must fail this test loudly, not silently let the test
        // finish green while one more scripted response is left behind.
        var responseDone = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > toolResponse.Sequence && f.Type == "response.done", FrameTimeout, ct);
        Assert.True(responseDone is not null, $"Expected this turn's own response.done within {FrameTimeout} before disposing the connection.");
    });

    /// <summary>
    /// The out-of-mode gate (tools.py's `item_out_of_mode`, shared verbatim by both pipelines --
    /// see cascade_processor.py's `_execute_tool_call` dispatching into the SAME `tools.py`
    /// functions `rtmt.tools` already wires up) rejects a breakfast-only item on a lunch-bound
    /// cascade session with the exact same structured shape the realtime pipeline's own
    /// <c>Breakfast_item_is_rejected_as_out_of_mode_when_connected_in_lunch_mode</c> proves.
    ///
    /// Mutation-check note: this specific row (mode="lunch") is NOT, by itself, the proof that
    /// `?mode=` reaches `create_session` -- `order_state.OrderState.create_session` defaults an
    /// unbound/invalid mode to `"lunch"` for any `features.dayparts` persona (see that method's
    /// own comment), so reverting `handle()`'s `menu_mode=requested_menu_mode` pass-through still
    /// leaves this session bound to "lunch" by the fallback default and this row keeps passing.
    /// The real proof that the pass-through is wired up is the sibling
    /// <see cref="Cascade_session_bound_to_breakfast_mode_accepts_the_breakfast_only_meal"/> row
    /// (mode="breakfast", which is NOT the fallback default) -- confirmed to fail under that exact
    /// mutation. This row instead pins the REJECTION wire shape itself (tools.py's `update_order`
    /// returns `ToolResultDirection.TO_SERVER` on rejection, so -- exactly like the realtime
    /// pipeline's own out-of-mode test asserts via `CallToolAsync(..., toClient: false)` -- no
    /// `extension.middle_tier_tool_response(update_order)` frame is ever sent to the client for
    /// the rejection itself; only the model (TO_SERVER) sees the structured rejection JSON). The
    /// guest-facing proof is the SAME one the realtime test uses: a follow-up `get_order` call
    /// (tools.py's `get_order` is `TO_BOTH`, so it DOES reach the client) shows the order is
    /// untouched (0 items) -- scripted here as the model's own natural next tool call after a
    /// rejection, which the fake chat-completions queue drives deterministically.
    /// </summary>
    [Fact]
    public Task Cascade_session_bound_to_lunch_mode_rejects_the_breakfast_only_meal_as_out_of_mode() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, persona: Persona, mode: "lunch");
        await using var browser = connection.Browser;

        fixture.Chat.EnqueueMessage(ToolCallMessage(
            "call_add_breakfast_in_lunch", "update_order",
            """{"action":"add","item_name":"Delta Breakfast Meal","size":"regular","quantity":1}"""));
        fixture.Chat.EnqueueMessage(ToolCallMessage("call_get_after_rejection", "get_order", "{}"));
        fixture.Chat.EnqueueMessage(FinalMessage("Sorry, that's not available right now."));
        fixture.Realtime.NextTranscript = "I'd like a Delta Breakfast Meal.";

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var getOrderResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var name) && name.GetString() == "get_order",
            FrameTimeout, ct);
        Assert.True(getOrderResponse is not null, $"Expected an extension.middle_tier_tool_response(get_order) within {FrameTimeout}.");

        var order = JsonDocument.Parse(getOrderResponse!.Json.GetProperty("tool_result").GetString()!).RootElement;
        Assert.Equal(0, order.GetProperty("items").GetArrayLength());

        // Rick's PR #253 review item 2 (correcting this comment's own prior, wrong theory): the
        // real reason this drain matters is FakeChatCompletionsServer's scripted-response FIFO,
        // NOT any corruption of the shared Kestrel connection -- Rick confirmed via the barge-in
        // row's own ChatCompletionsRequest.Aborted proof that the connection itself is fine. The
        // actual bug: this test's own assertions above are already satisfied by round 2
        // (get_order), so disposing the WS here would abort round 3's /chat/completions request
        // while HandleCompletionAsync is still parsing its body -- which runs BEFORE the FIFO
        // dequeue (FakeChatCompletionsServer.cs ~220-242) -- leaving round 3's scripted "Sorry,
        // that's not available right now." message stuck in the queue, to be wrongly handed out
        // to an unrelated later request (e.g. the very next scenario's first /chat/completions
        // call in this shared fixture) instead of being consumed here. Draining to this turn's
        // own response.done means round 3 always actually completes and dequeues its own message,
        // so nothing is ever left behind for the next scenario to inherit --
        // ConformanceFixture.AssertNoPendingExtraFakeState (which this fixture overrides to call
        // Chat.AssertNoPendingScriptedResponses AFTER every scenario's body, including this one)
        // is the backstop that would now catch a regression here loudly instead of silently.
        var responseDone = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > getOrderResponse.Sequence && f.Type == "response.done", FrameTimeout, ct);
        Assert.True(responseDone is not null, $"Expected this turn's own response.done within {FrameTimeout} before disposing the connection.");
    });

    /// <summary>
    /// Rick's PR #253 review item 1: `extension.set_voice` must be rejected the same way
    /// realtime's own handler (rtmt.py's `_sanitize_voice` call) already rejects it -- an unknown
    /// voice must never be adopted into `state.voice`/the session store. Proven end to end here
    /// rather than only at the Python unit-test layer (`HandleClientMessageSetVoiceTests` in
    /// test_cascade_processor.py) because the wire-protocol contract is what actually matters: a
    /// forged `extension.set_voice` must not change which voice TTS speaks with for this guest's
    /// very next turn. Mutation-test seam: reverting `_handle_client_message`'s
    /// `extension.set_voice` branch back to `if voice: state.voice = voice` makes this assertion
    /// fail -- the next turn's TTS request would carry the forged voice instead of test-delta's
    /// own "alloy".
    /// </summary>
    [Fact]
    public Task Cascade_extension_set_voice_with_an_unknown_voice_is_dropped_not_adopted() => fixture.RunAsync(async () =>
    {
        const string forgedVoice = "rick_probe_voice";
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, persona: Persona);
        await using var browser = connection.Browser;

        await browser.SendExtensionSetVoiceAsync(forgedVoice, cancellationToken: ct);

        fixture.Chat.EnqueueMessage(FinalMessage("Sure thing!"));
        fixture.Realtime.NextTranscript = "Hello?";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var responseDone = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.done", FrameTimeout, ct);
        Assert.True(responseDone is not null, $"Expected this turn's own response.done within {FrameTimeout}.");

        Assert.DoesNotContain(forgedVoice, fixture.Realtime.TtsRequestVoices);
        Assert.Contains("alloy", fixture.Realtime.TtsRequestVoices);
    });
}

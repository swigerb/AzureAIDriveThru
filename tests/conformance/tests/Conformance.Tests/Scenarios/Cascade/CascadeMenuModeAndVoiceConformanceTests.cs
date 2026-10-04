using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// #248: the Python-cascade parity gaps fixed in <c>cascade_processor.py</c> (per-persona default
/// voice, `?mode=` daypart binding), proven end to end over the real wire protocol against
/// <see cref="CascadeMenuModeAndVoiceConformanceFixture"/>'s <c>test-delta</c> pack (same pack
/// <c>MenuModeConformanceTests</c> already proved out for the realtime pipeline).
///
/// Left UNTAGGED (no <c>[Trait("Dotnet", "ready")]</c>): PR #236 (the C# cascade pipeline port,
/// issue #13 Wave 5) was not merged as of this PR, so `app/backend-dotnet` has no
/// `CascadeProcessor.cs` to run these rows against yet. Tag these `Dotnet="ready"` once #236
/// merges AND the dotnet cascade pipeline implements the same per-persona voice/mode binding this
/// PR ports on the Python side (#236's own PR description already shows the C# cascade has both
/// -- this is a parity port onto the OTHER leg, not new C# work).
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
    });
}

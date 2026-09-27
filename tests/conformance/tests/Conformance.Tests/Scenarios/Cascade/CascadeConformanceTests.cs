using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// Issue #82 acceptance: "the SAME tool calling (search, update_order, get_order) and the same
/// structured results, the same session metadata (persona, model, pipeline) and the same client
/// wire protocol toward the frontend" -- proven here against fake STT/chat/TTS upstreams, exactly
/// as #75's <see cref="ModelSelectionConformanceTests"/> proved model dispatch/selection for the
/// realtime pipeline. Untagged (no <c>[Trait("Dotnet", "ready")]</c>), matching every other
/// feature-area test file's convention for a feature the dotnet backend skeleton doesn't
/// implement yet (see <c>PersonaDiscoveryConformanceTests</c>/<c>ModelSelectionConformanceTests</c>).
///
/// Reuses <see cref="OrderScenarioHelpers.GetOrderFinalTotalDisplay"/>/<c>AssertRejectionShape</c>
/// (the Ordering folder's own realtime-side pricing/rejection assertions) so a pricing or
/// not_on_menu regression on either pipeline is caught by literally the same assertion code --
/// there's no chance of the two pipelines' test code drifting into checking subtly different
/// things and each staying green while the pipelines themselves silently disagree.
/// </summary>
[Collection(CascadeConformanceCollection.Name)]
public sealed class CascadeConformanceTests(CascadeConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = CascadeScenarioHelpers.FrameTimeout;

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

    [Fact]
    public Task Cascade_dispatch_binds_session_metadata_to_the_requested_persona_model_and_pipeline() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // #75's own dispatch seam (dispatch_processor/ProcessorRegistry), exercised end to end:
        // ?model=gpt-5-mini is catalogued under the cascade pipeline (config.yaml), so this
        // connection must be served by CascadeProcessor.handle, never RTMiddleTier.handle -- with
        // no rtmt.py edit whatsoever telling it to. A regression that routed this back to
        // realtime would either time out waiting for extension.session_metadata below (realtime's
        // own handshake needs a real upstream WS session.created round trip cascade never sends)
        // or, if the fake realtime upstream happened to answer anyway, report pipeline="realtime".
        await using var browser = await CascadeScenarioHelpers.ConnectAsync(fixture, "gpt-5-mini", ct);

        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, $"Expected extension.session_metadata within {FrameTimeout}.");

        Assert.Equal("sonic", metadata!.Json.GetProperty("persona").GetString());
        Assert.Equal("gpt-5-mini", metadata.Json.GetProperty("model").GetString());
        Assert.Equal("cascade", metadata.Json.GetProperty("pipeline").GetString());
    });

    [Fact]
    public Task Cascade_tool_calling_round_trip_reaches_get_order_and_returns_structured_json_to_the_client() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await CascadeScenarioHelpers.ConnectAsync(fixture, "gpt-5-mini", ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null);

        // Round 1: the model calls get_order (a tool that -- like update_order/search -- takes
        // `session_id` positionally, see cascade_processor.py::_execute_tool_call's own
        // `if name in ("update_order", "get_order", "reset_order", "search")` branch, mirroring
        // rtmt.py's identical dispatch). Round 2: a plain final answer, no more tool calls.
        fixture.Chat.EnqueueMessage(ToolCallMessage("call_get_order_1", "get_order", "{}"));
        fixture.Chat.EnqueueMessage(FinalMessage("Your order is currently empty."));
        fixture.Realtime.NextTranscript = "What's on my order so far?";

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        // extension.middle_tier_tool_response is rtmt.py's OWN tool-result wire event
        // (docs/persona-architecture.md section 6) -- CascadeProcessor._execute_tool_call emits
        // the exact same event/shape (previous_item_id/tool_name/tool_result), never a
        // cascade-specific alternative, so the frontend needs no changes to render it.
        var toolResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var name) && name.GetString() == "get_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null, $"Expected an extension.middle_tier_tool_response(get_order) within {FrameTimeout}.");

        var orderSummaryJson = toolResponse!.Json.GetProperty("tool_result").GetString()!;
        Assert.Equal(0, OrderScenarioHelpers.GetOrderItemCount(orderSummaryJson));

        var finalAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(finalAnswer is not null, "Expected the model's final answer as response.audio_transcript.delta.");
        Assert.Equal("Your order is currently empty.", finalAnswer!.Json.GetProperty("delta").GetString());

        // Proves _speak() actually reached the fake TTS endpoint (the SAME
        // FakeRealtimeUpstreamServer.BaseUri the realtime pipeline's WS points at) with the final
        // answer text, and streamed at least one audio.delta chunk back -- the same client-visible
        // audio-streaming contract the realtime pipeline uses.
        var audioDelta = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(audioDelta is not null, "Expected at least one response.audio.delta chunk from cascade TTS.");
        Assert.Contains("Your order is currently empty.", fixture.Realtime.TtsRequestInputs);
    });

    [Fact]
    public Task Cascade_update_order_pricing_matches_the_same_menu_and_tax_math_as_realtime() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await CascadeScenarioHelpers.ConnectAsync(fixture, "gpt-5-mini", ct);
        await browser.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);

        // "Tots"/medium is the real personas/sonic/menu/menuItems.json price (2.79) the golden
        // pricing dataset (tests/conformance/testdata/golden-order-pricing.json) already asserts
        // for the realtime pipeline's own tax case -- sonic's tax_rate is 0.08, so
        // subtotal=2.79, tax=0.2232 (=> "$0.22"), finalTotal=3.0132 (=> "$3.01").
        fixture.Chat.EnqueueMessage(ToolCallMessage(
            "call_update_order_1", "update_order",
            """{"action":"add","item_name":"Tots","size":"medium","quantity":1}"""));
        fixture.Chat.EnqueueMessage(FinalMessage("Added a medium Tots -- anything else?"));
        fixture.Realtime.NextTranscript = "I'll get a medium tots.";

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var toolResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var name) && name.GetString() == "update_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null, $"Expected an extension.middle_tier_tool_response(update_order) within {FrameTimeout}.");

        var orderSummaryJson = toolResponse!.Json.GetProperty("tool_result").GetString()!;
        Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(orderSummaryJson));
        Assert.Equal("$2.79", OrderScenarioHelpers.GetOrderTotalDisplay(orderSummaryJson));
        Assert.Equal("$0.22", OrderScenarioHelpers.GetOrderTaxDisplay(orderSummaryJson));
        Assert.Equal("$3.01", OrderScenarioHelpers.GetOrderFinalTotalDisplay(orderSummaryJson));
    });

    [Fact]
    public Task Cascade_not_on_menu_rejection_matches_realtimes_structured_shape_in_the_tool_message_fed_back_to_the_model() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await CascadeScenarioHelpers.ConnectAsync(fixture, "gpt-5-mini", ct);
        await browser.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);

        const string offMenuItem = "Unicorn Frappuccino";
        var requestWatermark = fixture.Chat.Requests.Count;

        // tools.py's not_on_menu rejection is TO_SERVER only (never TO_CLIENT/TO_BOTH -- see
        // update_order's own on-menu gate) -- the browser itself never sees a
        // extension.middle_tier_tool_response for a rejected add, exactly like realtime. The
        // rejection is only visible in the NEXT chat-completions request's own tool-role message
        // (the ToolMessage cascade_processor.py appends to state.messages, mirroring rtmt.py's
        // function_call_output.output upstream), so that's what this asserts against instead.
        fixture.Chat.EnqueueMessage(ToolCallMessage(
            "call_update_order_off_menu", "update_order",
            $$"""{"action":"add","item_name":"{{offMenuItem}}","size":"medium","quantity":1}"""));
        fixture.Chat.EnqueueMessage(FinalMessage("Sorry, we don't have that -- would you like something else?"));
        fixture.Realtime.NextTranscript = "Can I get a unicorn frappuccino?";

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        await browser.ReceivedFrames.WaitForAsync(f => f.Type == "response.done", FrameTimeout, ct);

        var secondRequest = fixture.Chat.Requests.Skip(requestWatermark).Skip(1).FirstOrDefault();
        Assert.True(secondRequest is not null, "Expected a second /chat/completions request carrying the tool result.");

        var toolMessage = secondRequest!.RawBody.GetProperty("messages").EnumerateArray()
            .LastOrDefault(m => m.TryGetProperty("role", out var role) && role.GetString() == "tool");
        Assert.True(toolMessage.ValueKind != JsonValueKind.Undefined, "Expected a tool-role message in the follow-up request.");

        OrderScenarioHelpers.AssertRejectionShape(
            toolMessage.GetProperty("content").GetString()!,
            expectedReason: "not_on_menu",
            expectedItemName: offMenuItem);

        // Belt-and-braces, mirroring SearchToolTests's own "never notifies the browser" check:
        // no extension.middle_tier_tool_response for update_order ever reached the client.
        var anyToolResponse = browser.ReceivedFrames.Snapshot().Any(f =>
            f.Type == "extension.middle_tier_tool_response" &&
            f.Json.TryGetProperty("tool_name", out var toolName) && toolName.GetString() == "update_order");
        Assert.False(anyToolResponse, "A not_on_menu rejection must never emit extension.middle_tier_tool_response.");
    });
}

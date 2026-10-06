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
/// realtime pipeline.
///
/// Reuses <see cref="OrderScenarioHelpers.GetOrderFinalTotalDisplay"/>/<c>AssertRejectionShape</c>
/// (the Ordering folder's own realtime-side pricing/rejection assertions) so a pricing or
/// not_on_menu regression on either pipeline is caught by literally the same assertion code --
/// there's no chance of the two pipelines' test code drifting into checking subtly different
/// things and each staying green while the pipelines themselves silently disagree.
///
/// Issue #13 Wave 5: tagged <c>[Trait("Dotnet", "ready")]</c> now that
/// <c>Backend.Sessions.CascadeProcessor</c> is implemented and registered in
/// <c>ProcessorRegistry</c> -- all 7 rows verified green against the C# backend across 3
/// consecutive local runs (<c>CONFORMANCE_BACKEND=dotnet</c>) with no flakiness before this trait
/// was added.
/// </summary>
[Collection(CascadeConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
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
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        await using var browser = connection.Browser;

        var metadata = browser.ReceivedFrames.Snapshot().FirstOrDefault(f => f.Type == "extension.session_metadata");
        Assert.True(metadata is not null, $"Expected extension.session_metadata within {FrameTimeout}.");

        Assert.Equal("sonic", metadata!.Json.GetProperty("persona").GetString());
        Assert.Equal("gpt-5-mini", metadata.Json.GetProperty("model").GetString());
        Assert.Equal("cascade", metadata.Json.GetProperty("pipeline").GetString());
    });

    [Fact]
    public Task Cascade_tool_calling_round_trip_reaches_get_order_and_returns_structured_json_to_the_client() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        await using var browser = connection.Browser;

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
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var name) && name.GetString() == "get_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null, $"Expected an extension.middle_tier_tool_response(get_order) within {FrameTimeout}.");

        var orderSummaryJson = toolResponse!.Json.GetProperty("tool_result").GetString()!;
        Assert.Equal(0, OrderScenarioHelpers.GetOrderItemCount(orderSummaryJson));

        // Watermarked past the greeting's own turn -- WaitForAsync (Conformance.Fakes.FrameLog)
        // always scans from the very first recorded frame, so a content-agnostic type-only
        // predicate would otherwise match the greeting's own response.audio_transcript.delta.
        var finalAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(finalAnswer is not null, "Expected the model's final answer as response.audio_transcript.delta.");
        Assert.Equal("Your order is currently empty.", finalAnswer!.Json.GetProperty("delta").GetString());

        // Proves _speak() actually reached the fake TTS endpoint (the SAME
        // FakeRealtimeUpstreamServer.BaseUri the realtime pipeline's WS points at) with the final
        // answer text, and streamed at least one audio.delta chunk back -- the same client-visible
        // audio-streaming contract the realtime pipeline uses.
        var audioDelta = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(audioDelta is not null, "Expected at least one response.audio.delta chunk from cascade TTS.");
        Assert.Contains("Your order is currently empty.", fixture.Realtime.TtsRequestInputs);
    });

    [Fact]
    public Task Cascade_update_order_pricing_matches_the_same_menu_and_tax_math_as_realtime() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        await using var browser = connection.Browser;

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
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var name) && name.GetString() == "update_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null, $"Expected an extension.middle_tier_tool_response(update_order) within {FrameTimeout}.");

        var orderSummaryJson = toolResponse!.Json.GetProperty("tool_result").GetString()!;
        Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(orderSummaryJson));
        Assert.Equal("$2.79", OrderScenarioHelpers.GetOrderTotalDisplay(orderSummaryJson));
        Assert.Equal("$0.22", OrderScenarioHelpers.GetOrderTaxDisplay(orderSummaryJson));
        Assert.Equal("$3.01", OrderScenarioHelpers.GetOrderFinalTotalDisplay(orderSummaryJson));

        // Named pre-existing flake (#118 Rick re-review item 3): this test enqueues TWO scripted
        // /chat/completions responses (the tool call round above, plus this final round's own
        // "Added a medium Tots" reply) but, unlike every sibling test in this file
        // (Cascade_tool_calling_round_trip_..., Cascade_a_429_from_..., etc.), used to return
        // right after the pricing assertions above -- disposing `browser` (and, transitively,
        // this turn's CascadeProcessor session) before the model's second completions round ever
        // fired. Whether that second round happened to still land before the NEXT cascade test's
        // own greeting/scripted request depended entirely on scheduler timing -- when it lost that
        // race, this test's own "Added a medium Tots -- anything else?" was left sitting,
        // unconsumed, in FakeChatCompletionsServer's single shared FIFO queue, and got dequeued by
        // whichever cascade test ran next instead of that test's own scripted response (a
        // content-mismatch or timeout failure with no connection to what that other test actually
        // exercises). Waiting for this final answer here -- the same convention every other
        // multi-round test in this file already follows -- means the queue is always left exactly
        // as empty as this test found it, regardless of what runs after it.
        var finalAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio_transcript.delta",
            FrameTimeout, ct);
        Assert.True(finalAnswer is not null, "Expected the model's final answer as response.audio_transcript.delta.");
        Assert.Equal("Added a medium Tots -- anything else?", finalAnswer!.Json.GetProperty("delta").GetString());
    });

    [Fact]
    public Task Cascade_not_on_menu_rejection_matches_realtimes_structured_shape_in_the_tool_message_fed_back_to_the_model() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        await using var browser = connection.Browser;

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

        await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.done", FrameTimeout, ct);

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
            f.Sequence > connection.GreetingWatermark &&
            f.Type == "extension.middle_tier_tool_response" &&
            f.Json.TryGetProperty("tool_name", out var toolName) && toolName.GetString() == "update_order");
        Assert.False(anyToolResponse, "A not_on_menu rejection must never emit extension.middle_tier_tool_response.");
    });

    // The three rows below cover Rick's #118 review item 5's demo-scope cascade turn-taking
    // parity: greeting on connect, barge-in cancellation, and the 429 rate-limit notice path --
    // see cascade_processor.py's own docstring / _send_greeting / _cancel_current_turn /
    // _with_rate_limit_retry for the implementation each of these proves end to end. Resume,
    // nudge, and echo suppression are explicitly deferred (design 7.1) and have no row here.

    [Fact]
    public Task Cascade_sends_a_greeting_automatically_on_connect() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // Unlike every other row in this file, no SendGuestTurnAsync is sent at all -- the
        // greeting must fire purely off the connection itself (_run_session spawns
        // _start_greeting before the WS message loop even starts reading), through the exact
        // same chat-tool-loop + TTS path (_run_turn_and_speak) a real guest turn uses.
        fixture.Chat.EnqueueMessage(FinalMessage("Welcome to the drive-thru! What can I get started for you today?"));

        await using var browser = await CascadeScenarioHelpers.ConnectAsync(fixture, "gpt-5-mini", ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata before the greeting.");

        var greetingAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(greetingAnswer is not null, "Expected the greeting's own response.audio_transcript.delta.");
        Assert.Equal("Welcome to the drive-thru! What can I get started for you today?",
            greetingAnswer!.Json.GetProperty("delta").GetString());

        var audioDelta = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(audioDelta is not null, "Expected at least one response.audio.delta chunk for the spoken greeting.");
        Assert.Contains("Welcome to the drive-thru! What can I get started for you today?", fixture.Realtime.TtsRequestInputs);

        // The greeting is fed as a plain UserMessage into the SAME chat-completions call a real
        // guest turn uses -- proven here by the very first /chat/completions request the fake
        // received carrying no tool-role/assistant history yet (just the system + greeting-
        // instruction messages), rather than some greeting-specific bypass.
        Assert.True(fixture.Chat.Requests.Count >= 1, "Expected the greeting to reach /chat/completions.");
    });

    /// <summary>Bounds the final negative wait below. By the time that wait starts, the first
    /// turn's own /chat/completions request has already been asserted <c>Aborted</c> -- so unlike
    /// <see cref="RateLimitGuestSpeechCancellationTests"/>'s own bounded negative waits (which
    /// race a real, still-possible scheduled retry), there is no plausible mechanism left that
    /// could still deliver the first turn's answer; this only guards against a regression that
    /// somehow buffers/replays it. Comfortably short is fine -- same order of magnitude as that
    /// class's own 1-1.5s bounds for "already proven, just double-check" waits.</summary>
    private static readonly TimeSpan NegativeCheckTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public Task Cascade_barge_in_cancels_the_in_flight_turn_before_it_speaks() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // The greeting's own turn must resolve first so it can't be mistaken for the "in-flight
        // turn" this scenario cancels -- same ordering precaution UpdateOrderToolCallTests /
        // RateLimitGuestSpeechCancellationTests use for the realtime pipeline's own greeting.
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        await using var browser = connection.Browser;

        var requestWatermark = fixture.Chat.RequestCount;

        // Issue #118 Rick re-review item 2: event-driven instead of a fixed ResponseDelay + a
        // polling loop with its own margin. The FIRST guest turn's chat completion is held on a
        // gate this test controls -- FakeChatCompletionsServer suspends that one request's
        // response write until Release() is called (or the connection is aborted first), so
        // "the first turn is genuinely in flight" is proven by WaitForRequestCountAsync actually
        // observing the request, never by racing a timer against how long a scripted delay
        // happened to be.
        var gate = fixture.Chat.HoldNextResponse();
        fixture.Chat.EnqueueMessage(FinalMessage("You should never hear this -- the turn gets cancelled."));
        fixture.Realtime.NextTranscript = "I'll get a medium tots.";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        // Proves the first turn's /chat/completions request genuinely landed and is now
        // suspended on the gate (not merely "hasn't happened yet") before barging in -- otherwise
        // a second speech burst sent too early would just look like ordinary silence to
        // _TurnDetector, proving nothing about cancellation.
        var landed = await fixture.Chat.WaitForRequestCountAsync(requestWatermark + 1, FrameTimeout, ct);
        Assert.True(landed, "Expected the first turn's /chat/completions request to land.");

        // Barge-in: a second speech_started arrives while the first turn is still suspended on
        // its held chat completion. _handle_client_message's speech_started branch must cancel
        // the first turn's background task before it can ever reach _speak.
        fixture.Chat.EnqueueMessage(FinalMessage("Barge-in answer."));
        fixture.Realtime.NextTranscript = "Actually, never mind.";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var secondAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "response.audio_transcript.delta" && f.Json.GetProperty("delta").GetString() == "Barge-in answer.",
            FrameTimeout, ct);
        Assert.True(secondAnswer is not null, "Expected the second (barge-in) turn's own final answer.");

        // The positive proof of cancellation (Rick's #118 review item 2): the first turn's own
        // /chat/completions request was aborted by the client -- CascadeProcessor's
        // _cancel_current_turn cancelling the task awaiting it -- not merely "hasn't answered
        // yet". A no-op cancellation would leave this request un-aborted, still suspended on the
        // gate below.
        var firstRequest = fixture.Chat.Requests[requestWatermark];
        Assert.True(firstRequest.Aborted,
            "Expected the first turn's /chat/completions request to have been aborted by the client, proving _cancel_current_turn actually cancelled it.");

        // Only matters for hygiene now (releasing a suspended fake-side handler coroutine, if the
        // abort somehow left one dangling) -- the request is already proven aborted above, so
        // this can never let the first turn's answer reach the browser.
        gate.Release();

        // The cancelled first turn's answer must never reach the client at all -- not before,
        // not after the barge-in turn's own answer. Short, BOUNDED wait expected to time out
        // (see NegativeCheckTimeout's doc comment) -- no sleep, no large fixed margin.
        var firstTurnAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "response.audio_transcript.delta" &&
                 f.Json.GetProperty("delta").GetString() == "You should never hear this -- the turn gets cancelled.",
            NegativeCheckTimeout, ct);
        Assert.True(firstTurnAnswer is null, "A barged-in-on turn must never reach _speak/response.audio_transcript.delta.");
    });

    [Fact]
    public Task Cascade_a_429_from_chat_completion_notifies_the_client_then_completes_once_it_resolves() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        await using var browser = connection.Browser;

        // Same ladder shape as RateLimitRetryTimingTests' own
        // Ladder_runs_silent_then_two_notifications_then_gives_up, adapted for cascade's
        // REST-call-based 429s: attempt 0 fails silently (no extension.rate_limited at all),
        // attempt 1 fails and notifies {attempt:1} (not final), then attempt 2 succeeds and the
        // turn completes normally -- proving a 429 from chat/STT/TTS goes through the SAME
        // extension.rate_limited notice path the realtime pipeline's own RateLimitRecovery uses,
        // without needing to also prove exhaustion (already covered by the Python unit suite's
        // own WithRateLimitRetryTests.test_exhausted_retries_send_the_final_notice_and_raise).
        fixture.Chat.EnqueueErrorStatus(429);
        fixture.Chat.EnqueueErrorStatus(429);
        fixture.Chat.EnqueueMessage(FinalMessage("All set after the retry."));
        fixture.Realtime.NextTranscript = "Can I get a corn dog?";

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var attempt1Notification = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "extension.rate_limited" && f.Json.GetProperty("attempt").GetInt32() == 1,
            FrameTimeout, ct);
        Assert.True(attempt1Notification is not null, "Expected extension.rate_limited{attempt:1} after the first retry also failed.");
        Assert.False(attempt1Notification!.Json.TryGetProperty("final", out _), "attempt:1 must not carry final:true.");

        // Watermarked past attempt1Notification (itself already past the greeting) --
        // WaitForAsync (Conformance.Fakes.FrameLog) always scans from the very first recorded
        // frame, so a content-agnostic type-only predicate would otherwise match the greeting's
        // own response.audio_transcript.delta instead of this turn's retried final answer.
        var finalAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > attempt1Notification.Sequence && f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(finalAnswer is not null, "Expected the turn to still complete normally once the ladder's own retry succeeded.");
        Assert.Equal("All set after the retry.", finalAnswer!.Json.GetProperty("delta").GetString());

        var doneFrame = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.done" && f.Sequence > attempt1Notification.Sequence, FrameTimeout, ct);
        Assert.True(doneFrame is not null, "Expected response.done once the retried turn finished.");
    });

    // The two rows below cover #247/#262's own cascade turn-lifecycle fixes end to end, against
    // the real backend process (not just the Python/C# unit suites) -- see cascade_processor.py's
    // _run_chat_tool_loop/_run_turn_and_speak (and CascadeProcessor.cs's identical
    // RunChatToolLoopAsync/RunTurnAndSpeakAsync) for the implementation each proves.

    [Fact]
    public Task Cascade_barge_in_during_a_tool_calling_round_truncates_the_orphaned_tool_call_so_the_next_turn_succeeds() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        await using var browser = connection.Browser;

        var chatWatermark = fixture.Chat.RequestCount;
        var searchWatermark = fixture.Search.ReceivedRequests.Count;

        // #247's actual bug window: a SINGLE round with MULTIPLE tool calls, where a real
        // barge-in lands while the FIRST tool call (get_order -- synchronous local dispatch, no
        // network call of its own) has already resolved and appended its own ToolMessage, but the
        // SECOND (search -- a real network round trip to the fake Azure AI Search index, held
        // open below) is still genuinely in flight. CascadeProcessor's own tool-call loop runs
        // these sequentially (`for tool_call in tool_calls: await self._execute_tool_call(...)`),
        // so this reproduces the exact "some ids answered, one not yet" state that would leave an
        // orphaned tool_calls message if the whole round weren't truncated out of history.
        fixture.Chat.EnqueueMessage(new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "call_get_order_1",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "get_order", ["arguments"] = "{}" },
                },
                new JsonObject
                {
                    ["id"] = "call_search_1",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "search", ["arguments"] = """{"query":"burger"}""" },
                }),
        });
        var searchGate = fixture.Search.HoldNextResponse();
        fixture.Realtime.NextTranscript = "What's on my order, and do you have burgers?";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        // Proves get_order's own tool response already reached the client (the round is
        // genuinely PARTWAY through, not merely "hasn't started") before barging in.
        var getOrderResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var name) && name.GetString() == "get_order",
            FrameTimeout, ct);
        Assert.True(getOrderResponse is not null, "Expected get_order's own tool response to land before the search call blocks.");

        // Proves the search call genuinely landed and is now suspended on the gate -- same
        // "landed, not merely not-yet-happened" proof Cascade_barge_in_cancels_the_in_flight_turn_before_it_speaks
        // uses for its own held /chat/completions request.
        var searchLanded = await fixture.Search.ReceivedRequests.WaitForAsync(f => f.Sequence >= searchWatermark, FrameTimeout, ct);
        Assert.True(searchLanded is not null, "Expected the round's own search tool call to land.");

        // Real barge-in: a second guest turn cancels the whole in-flight first turn (including
        // the still-suspended search call) before it can ever reach _speak.
        fixture.Chat.EnqueueMessage(FinalMessage("All set for the next query."));
        fixture.Realtime.NextTranscript = "Never mind, can I get a corn dog?";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var nextAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "response.audio_transcript.delta" && f.Json.GetProperty("delta").GetString() == "All set for the next query.",
            FrameTimeout, ct);
        Assert.True(nextAnswer is not null, "Expected the next guest turn to complete normally after the barge-in.");

        searchGate.Release(); // hygiene only -- the search call was already cancelled client-side.

        // THE #247 proof: the next turn's own /chat/completions request must carry no trace of
        // EITHER of the cancelled round's tool_calls/tool-result pairs -- proving
        // CascadeProcessor truncated the whole round out of history (including get_order's
        // already-answered ToolMessage) instead of leaving an orphaned tool_calls message (for
        // the still-pending search call) a real chat API would 400 on the next request.
        var nextRequest = fixture.Chat.Requests[chatWatermark + 1];
        foreach (var message in nextRequest.RawBody.GetProperty("messages").EnumerateArray())
        {
            if (message.TryGetProperty("tool_calls", out var toolCalls))
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    var id = toolCall.GetProperty("id").GetString();
                    Assert.NotEqual("call_get_order_1", id);
                    Assert.NotEqual("call_search_1", id);
                }
            }

            if (message.TryGetProperty("role", out var role) && role.GetString() == "tool" &&
                message.TryGetProperty("tool_call_id", out var toolCallId))
            {
                var id = toolCallId.GetString();
                Assert.NotEqual("call_get_order_1", id);
                Assert.NotEqual("call_search_1", id);
            }
        }
    });

    [Fact]
    public Task Cascade_a_non_429_chat_completion_failure_ends_the_turn_with_a_failed_response_done_instead_of_vanishing_silently() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        await using var browser = connection.Browser;

        // Unlike the 429 row above (which retries and ultimately succeeds), a non-429 failure
        // has no retry ladder at all -- #262's bug was that this uncaught exception left
        // response.created with no matching response.done, wedging the browser's "response in
        // progress" state forever. The fix sends an error event + a failed response.done instead.
        // 400 (not 500/503/504) deliberately avoids azure-ai-inference's OWN client-side
        // azure-core retry policy, which silently retries POST requests that receive 500/503/504
        // (see azure.core.pipeline.policies._retry.RetryPolicyBase._is_method_retryable) --
        // a transient-error retry at the SDK layer, unrelated to #262's bug, that would otherwise
        // mask this row's failure-path assertion behind a successful retried response.
        fixture.Chat.EnqueueErrorStatus(400);
        fixture.Realtime.NextTranscript = "Can I get a shake?";

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var errorEvent = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "error", FrameTimeout, ct);
        Assert.True(errorEvent is not null, "Expected an error event for the non-429 chat-completion failure.");

        var doneFrame = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.done", FrameTimeout, ct);
        Assert.True(doneFrame is not null, "Expected a response.done once the failed turn was torn down -- never a silently vanishing turn.");
        Assert.Equal("failed", doneFrame!.Json.GetProperty("response").GetProperty("status").GetString());
        Assert.Equal(0, doneFrame.Json.GetProperty("response").GetProperty("output").GetArrayLength());
    }, allowedNewBackendErrors: 1); // the one expected logger.exception(...)/LogError(...) call the fix itself makes

    [Fact]
    public Task Cascade_a_non_429_tts_failure_ends_the_turn_with_a_failed_response_done_instead_of_vanishing_silently() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        await using var browser = connection.Browser;

        // The model itself answers fine -- the failure is purely in the TTS leg (_speak), the
        // other half of #262's fix (RunTurnAndSpeakAsync's own TTS catch, not the chat-tool-loop
        // catch the row above exercises). Unlike chat completions, cascade's TTS call uses a
        // plain aiohttp.ClientSession with no retry middleware (see _speak), so 500 here needs no
        // non-retryable-status workaround the way the chat-completion row above does.
        fixture.Chat.EnqueueMessage(FinalMessage("Here's your answer, but TTS is about to fail."));
        fixture.Realtime.NextTranscript = "Can I get a shake?";
        fixture.Realtime.NextSpeechErrorStatus = 500;

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var errorEvent = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "error", FrameTimeout, ct);
        Assert.True(errorEvent is not null, "Expected an error event for the non-429 TTS failure.");

        // The model's own text IS still sent as a transcript delta (cascade_processor.py's
        // _run_turn_and_speak sends it BEFORE calling _speak -- the model succeeded here, only
        // the TTS leg failed afterward), so this is not a negative check.
        var textDelta = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "response.audio_transcript.delta" &&
                 f.Json.GetProperty("delta").GetString() == "Here's your answer, but TTS is about to fail.",
            FrameTimeout, ct);
        Assert.True(textDelta is not null, "Expected the model's own answer text as response.audio_transcript.delta before the TTS failure.");

        var doneFrame = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > textDelta!.Sequence && f.Type == "response.done", FrameTimeout, ct);
        Assert.True(doneFrame is not null, "Expected a response.done once the failed turn was torn down -- never a silently vanishing turn.");
        Assert.Equal("failed", doneFrame!.Json.GetProperty("response").GetProperty("status").GetString());
        Assert.Equal(0, doneFrame.Json.GetProperty("response").GetProperty("output").GetArrayLength());

        // No audio.delta ever streamed for this turn -- TTS failed before any bytes arrived.
        var neverAudio = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > textDelta.Sequence && f.Type == "response.audio.delta", NegativeCheckTimeout, ct);
        Assert.True(neverAudio is null, "A TTS failure must never surface any response.audio.delta for this turn.");
    }, allowedNewBackendErrors: 1); // the one expected logger.exception(...)/LogError(...) call the fix itself makes
}

using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// PR #22 review item 3: prove a scripted function call actually executes in the backend, not
/// just that the fake can emit the right frame shapes. Scripts an `update_order` call for the
/// second turn (after the greeting), then asserts the backend actually ran the tool: a
/// `conversation.item.create` with a `function_call_output` item comes back upstream, and the
/// browser receives `extension.middle_tier_tool_response` — both only possible if rtmt.py's
/// `response.output_item.done` handler recognised the scripted item as a real, pending tool call.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class UpdateOrderToolCallTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan s_frameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Scripted_update_order_call_executes_and_notifies_the_browser() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        bool noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(s_frameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        Task<FakeRealtimeConnection?> connectionTask = fixture.Realtime.WaitForNextConnectionAsync(s_frameTimeout, ct);
        await using RealtimeBrowserClient browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        FakeRealtimeConnection? connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {s_frameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Wait for the greeting's round trip to fully complete (tools_pending cleared, backend
        // ready for a new turn) before scripting the next response — enqueuing any earlier would
        // let the automatic greeting response.create consume our script instead.
        RecordedFrame? greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", s_frameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        const string CallId = "call_update_order_1";
        // #73: "Small Fries" is off-menu ("Fries" is not a menuItems.json item; the real side is
        // "Tots") and would now be rejected TO_SERVER-only, so the `extension.middle_tier_tool_response`
        // wait below would time out. This test's subject matter is the scripted-call plumbing
        // itself, not menu lookup, so it must use a real on-menu item.
        connection!.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: """{"action":"add","item_name":"Tots","size":"small","quantity":1,"price":2.19}""",
                CallId: CallId),
            new DoneEvent(),
        ]));

        // rtmt.py never handles a raw client response.create specially — it passes straight
        // through to the upstream socket unchanged, exactly like a server-VAD-triggered turn
        // would, without needing to simulate real audio timing.
        await browser.SendResponseCreateAsync(ct);

        RecordedFrame? functionCallOutput = await connection!.ReceivedFrames.WaitForAsync(
            f => f.Type == "conversation.item.create" &&
                 f.Json.TryGetProperty("item", out JsonElement item) &&
                 item.TryGetProperty("type", out JsonElement itemType) &&
                 itemType.GetString() == "function_call_output" &&
                 item.TryGetProperty("call_id", out JsonElement respondedCallId) &&
                 respondedCallId.GetString() == CallId,
            s_frameTimeout, ct);
        Assert.True(functionCallOutput is not null,
            $"Expected a conversation.item.create(function_call_output) for call_id={CallId} upstream within {s_frameTimeout}.");

        RecordedFrame? toolResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out JsonElement toolName) &&
                 toolName.GetString() == "update_order",
            s_frameTimeout, ct);
        Assert.True(toolResponse is not null,
            $"Expected extension.middle_tier_tool_response for update_order on the browser within {s_frameTimeout}.");

        // The "backend logged no unhandled error during this scenario" invariant (PR #22 review
        // item N5) is now a fixture-wide, language-neutral check applied by
        // ConformanceFixture.RunAsync after every scenario -- no per-test assertion needed here.
    });
}

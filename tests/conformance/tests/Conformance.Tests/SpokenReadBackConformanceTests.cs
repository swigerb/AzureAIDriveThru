using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #304, extended by #313 (Rick's REQUEST CHANGES review, finding 3.3): proves the
/// server-composed, mandatory <c>spokenReadBack</c> contract actually reaches the realtime model
/// itself, not merely the browser's client-only JSON channel.
///
/// <para>The original version of this file (pre-#313) only ever asserted on
/// <see cref="ToolCallResult.ToolResultJson"/> -- the <c>extension.middle_tier_tool_response</c>
/// frame the BROWSER receives, which the realtime model never sees at all (rtmt.py/
/// <c>MiddleTierRtmtHandler</c> only forward that channel to the client, never upstream). A
/// <c>spokenReadBack</c> field present in that JSON proves nothing about whether the voice model
/// itself ever hears the read-back it's supposed to speak verbatim. Every assertion below instead
/// reads <see cref="ToolCallResult.FunctionCallOutputText"/> -- the literal
/// <c>conversation.item.create</c>(<c>function_call_output</c>).<c>output</c> string sent
/// UPSTREAM, i.e. exactly what the realtime model receives as this tool call's result and the only
/// channel it can ever read from.</para>
///
/// <para>Also asserts the words-not-digits rendering from #313 item 2 (a bare digit quantity read
/// next to a count-based size is ambiguous on a voice channel). The Munchkins multi-line
/// read-back and Brian's exact modify-then-readback bug-report scenarios Rick's review called
/// out by name live in <see cref="SpokenReadBackZetaConformanceTests"/> below, against a
/// synthetic fixture pack rather than a real brand item.</para>
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class SpokenReadBackConformanceTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = OrderScenarioHelpers.FrameTimeout;

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Get_order_function_call_output_carries_the_server_composed_spoken_read_back() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) =
            await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // "Tots" is a real, single-size-ambiguous menu item on the default persona, already used by
        // UpdateOrderToolCallTests for the identical reason: on-menu, no combo/bundle
        // complications, so the read-back's grouping logic has exactly one line to describe.
        var addResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"small","quantity":2,"price":2.19}""",
            "call_spoken_readback_add", roundTripIndex, ct);

        var getOrderResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_spoken_readback_get", addResult.RoundTripIndex, ct);

        // The model-facing channel -- not the browser-only JSON -- is the one that actually
        // matters here (#313 finding 3.3).
        var spokenReadBackText = getOrderResult.FunctionCallOutputText;
        Assert.False(string.IsNullOrWhiteSpace(spokenReadBackText),
            "get_order's function_call_output text must never be empty once the order has at least one item.");
        // #313 item 2: quantities are spelled out as words on the realtime channel, never bare
        // digits, so "two" (not "2 ") must appear ahead of the item name.
        Assert.Contains("two ", spokenReadBackText);
        Assert.DoesNotContain("2 Small Tots", spokenReadBackText);
        Assert.Contains("Tots", spokenReadBackText);
        // Money is spoken in words on the realtime channel too (#313 item 2) -- never "$" digits.
        Assert.Contains("Your total is", spokenReadBackText);
        Assert.Contains("dollar", spokenReadBackText);
        Assert.DoesNotContain("$", spokenReadBackText);

        // The client-only JSON channel still independently carries the same field for the
        // browser's own order-summary UI -- still asserted, just no longer the ONLY assertion.
        Assert.NotNull(getOrderResult.ToolResultJson);
        using var summary = JsonDocument.Parse(getOrderResult.ToolResultJson!);
        Assert.True(summary.RootElement.TryGetProperty("spokenReadBack", out var clientSpokenReadBack),
            "Expected get_order's client-facing tool_result to also carry a spokenReadBack field (issue #304).");
        Assert.False(string.IsNullOrWhiteSpace(clientSpokenReadBack.GetString()));
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Update_order_function_call_output_also_carries_the_mandatory_read_back() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) =
            await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
        await using var _ = browser;

        // #313 finding 1.4: before this fix, ONLY get_order's result included the read-back --
        // update_order's own function_call_output was just the one-line delta ("Added 2 Tots --
        // your total is now $X.XX"), so a guest who kept adding items without ever calling
        // get_order would never hear a read-back at all. This is the regression under test.
        var addResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Tots","size":"small","quantity":1,"price":2.19}""",
            "call_update_order_readback_add", roundTripIndex, ct);

        var text = addResult.FunctionCallOutputText;
        Assert.Contains("Added", text);
        Assert.Contains("Your total is", text);
        Assert.Contains("Tots", text);
    });
}

/// <summary>
/// #313 (Rick's REQUEST CHANGES re-review, items 2/3): these two scenarios were previously
/// scripted against a real persona pack's own trademarked item (a real
/// guest-facing brand name), which forced a dedicated `rebrand_baseline.yaml` "increase" entry
/// just to keep the rebrand ratchet green for this one test file. They now run against
/// <see cref="ZetaConformanceFixture"/>'s own synthetic, TEST-ONLY count-sized item
/// ("ZORBS® Bite Treats", sizes "10 count"/"25 count", a <c>spokenName</c> override, and a
/// trademark-cased <c>sizes.spokenAs</c> key) -- the same case-sensitive pronunciation-rewrite
/// and word-spelled-quantity behavior, with no real brand word anywhere in this file and no
/// baseline entry required.
/// </summary>
[Collection(ZetaConformanceCollection.Name)]
public sealed class SpokenReadBackZetaConformanceTests(ZetaConformanceFixture fixture)
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Zorbs_multi_line_read_back_uses_the_spoken_pronunciation_on_both_lines() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) =
            await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: ZetaConformanceFixture.PersonaId);
        await using var _ = browser;

        // Synthetic, guest-facing brand-STYLE item names straight off test-zeta's own
        // menuItems.json -- "ZORBS®"/"Zorbs" raw catalog casing, both of which the
        // case-sensitive pronunciation lexicon and sizes.spokenAs mapping must correct to
        // "Zorbs" on the realtime channel the model actually reads from.
        var addFirst = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"ZORBS® Bite Treats","size":"10 count","quantity":1,"price":3.99}""",
            "call_zorbs_add_1", roundTripIndex, ct);

        var addSecond = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Zorbs","size":"25 count","quantity":1,"price":8.99}""",
            "call_zorbs_add_2", addFirst.RoundTripIndex, ct);

        var getOrderResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_zorbs_get", addSecond.RoundTripIndex, ct);

        var text = getOrderResult.FunctionCallOutputText;
        Assert.DoesNotContain("ZORBS", text);
        Assert.Contains("Zorb", text);
        Assert.Contains("10 Count", text);
        Assert.Contains("25 Count", text);
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Modify_then_readback_reflects_the_new_count_never_the_stale_one() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) =
            await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: ZetaConformanceFixture.PersonaId);
        await using var _ = browser;

        // Brian's exact bug report (coordinator brief): a guest adds 25 Count Zorbs, then
        // asks to change it down to 10 Count -- the realtime model spoke "I've upgraded you" and
        // the read-back that followed still described the ORIGINAL count. "Changed" (not
        // "Upgraded") is asserted on the delta line; the read-back below asserts the actual bug.
        var addResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"ZORBS® Bite Treats","size":"25 count","quantity":1,"price":8.99}""",
            "call_modify_readback_add", roundTripIndex, ct);
        Assert.Contains("25 Count", addResult.FunctionCallOutputText);

        var modifyResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"modify","item_name":"ZORBS® Bite Treats","size":"10 count","quantity":1,"price":3.99}""",
            "call_modify_readback_modify", addResult.RoundTripIndex, ct);

        // #313 item 1.7: the delta line itself must say "Changed", never "Upgraded" -- a resize
        // down to a SMALLER count is not an upgrade.
        Assert.Contains("Changed", modifyResult.FunctionCallOutputText);
        Assert.DoesNotContain("Upgraded", modifyResult.FunctionCallOutputText);

        var getOrderResult = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_modify_readback_get", modifyResult.RoundTripIndex, ct);

        var text = getOrderResult.FunctionCallOutputText;
        Assert.Contains("10 Count", text);
        Assert.DoesNotContain("25 Count", text);
    });
}

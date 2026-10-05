using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #102 review item 1 ("Add matching conformance rows if cheap"): the extras
/// allow/block gate end-to-end, over the real wire protocol, against the SAME two-pack fixture
/// (test-alpha/test-beta) app/backend/tests/test_persona_binding.py's
/// PersonaBusinessRuleIsolationTests already proves at the Python unit level. test-alpha and
/// test-beta are configured with OPPOSITE extras.allowedBaseCategories/blockedBaseCategories for
/// the identical "mains" category (see their persona.json), so the SAME update_order sequence
/// against a "mains" base item plus an isExtra item must be BLOCKED for one persona's live
/// session and ACCEPTED for the other's -- proving tools.py's update_order() reads the gate off
/// the bound session's own persona, not a shared module-level allow/block list, end to end
/// through rtmt.py's real tool-call plumbing (not just the Python function called directly).
///
/// This file now covers the cheap wire-level rows from the Python isolation suite: extras gating
/// plus the new session-scoped machine-override add-time behavior. It still does NOT duplicate the
/// whole Python matrix: search-time machine OOS annotation would need FakeSearchServer's
/// fixed-Sonic MenuIndex.cs taught to seed per-persona documents for test-alpha/test-beta, and the
/// greeting/nudge/role-name checks have no distinct wire surface beyond what
/// PersonaDiscoveryConformanceTests/ResumeRehydrationAndNudgeTests already cover structurally.
/// invalid_modifiers is exercised the same way update_order's on-menu gate already is elsewhere in
/// this suite and adds no new plumbing risk beyond what these rows already prove.
/// </summary>
[Collection(TwoPersonaConformanceCollection.Name)]
public sealed class PersonaBusinessRuleConformanceTests(TwoPersonaConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = OrderScenarioHelpers.FrameTimeout;

    private static async Task<(RealtimeBrowserClient Browser, FakeRealtimeConnection Connection, int RoundTripIndex)> ConnectAndGreetAsPersonaAsync(
        TwoPersonaConformanceFixture fixture, string persona, CancellationToken ct)
    {
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, persona: persona, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout} for persona={persona}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, $"Greeting round trip never completed for persona={persona}.");

        return (browser, connection!, greetingRoundTrip!.Json.GetProperty("roundTripIndex").GetInt32());
    }

    private static Task SendMachineStatusAsync(RealtimeBrowserClient browser, string machine, string status, CancellationToken ct) =>
        browser.SendAsync(new JsonObject
        {
            ["type"] = "extension.set_machine_status",
            ["machine"] = machine,
            ["status"] = status,
        }, ct);

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Extras_gate_blocks_test_alpha_own_extra_on_its_own_blocked_mains_category() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await ConnectAndGreetAsPersonaAsync(fixture, TwoPersonaConformanceFixture.PersonaA, ct);
        await using var _ = browser;

        var addBase = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser, [("add", "Alpha Burger", "small", 1, 3.99m)], roundTripIndex, ct, "call_alpha_base");

        // test-alpha's persona.json blocks extras on "mains" -- the add-extra attempt must return
        // TO_SERVER only (tools.py's ToolResultDirection.TO_SERVER apology branch), never reach
        // the browser as extension.middle_tier_tool_response.
        var blockedExtra = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Alpha Flavor Shot","size":"small","quantity":1,"price":0.59}""",
            "call_alpha_extra", addBase.RoundTripIndex, ct, toClient: false);

        var confirmOrder = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_alpha_get_order", blockedExtra.RoundTripIndex, ct);
        Assert.False(string.IsNullOrWhiteSpace(confirmOrder.ToolResultJson), "get_order must return a tool_result.");
        Assert.DoesNotContain("Alpha Flavor Shot", confirmOrder.ToolResultJson!);
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Extras_gate_accepts_test_beta_own_extra_on_the_same_category_name_test_alpha_blocks() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await ConnectAndGreetAsPersonaAsync(fixture, TwoPersonaConformanceFixture.PersonaB, ct);
        await using var _ = browser;

        var addBase = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser, [("add", "Beta Double Burger", "regular", 1, 4.49m)], roundTripIndex, ct, "call_beta_base");

        // test-beta's persona.json ALLOWS extras on "mains" -- the identical category name
        // test-alpha blocks -- so this add must succeed (TO_BOTH), unlike the previous test.
        var acceptedExtra = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Beta Cheese Sauce","size":"regular","quantity":1,"price":0.79}""",
            "call_beta_extra", addBase.RoundTripIndex, ct);

        Assert.Contains("Beta Cheese Sauce", acceptedExtra.ToolResultJson ?? "");
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Machine_override_up_allows_a_pack_down_machine_item_to_be_added() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await ConnectAndGreetAsPersonaAsync(fixture, TwoPersonaConformanceFixture.PersonaA, ct);
        await using var _ = browser;

        await SendMachineStatusAsync(browser, "soda_machine", "up", ct);
        var accepted = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser, [("add", "Alpha Cola", "small", 1, 1.99m)], roundTripIndex, ct, "call_alpha_machine_up");

        Assert.Contains("Alpha Cola", accepted.ToolResultJson ?? "");
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Machine_override_down_rejects_a_pack_up_machine_item_with_machine_unavailable() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await ConnectAndGreetAsPersonaAsync(fixture, TwoPersonaConformanceFixture.PersonaB, ct);
        await using var _ = browser;

        await SendMachineStatusAsync(browser, "soda_machine", "down", ct);
        var rejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            """{"action":"add","item_name":"Beta Root Beer","size":"regular","quantity":1,"price":2.29}""",
            "call_beta_machine_down", roundTripIndex, ct, toClient: false);

        var payload = JsonDocument.Parse(rejected.FunctionCallOutputText).RootElement;
        Assert.Equal("machine_unavailable", payload.GetProperty("reason").GetString());
        Assert.Equal("Beta Root Beer", payload.GetProperty("item_name").GetString());
    });
}

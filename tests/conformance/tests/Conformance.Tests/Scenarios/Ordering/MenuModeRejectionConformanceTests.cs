using System.Net;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Rick's PR 166 round-1 review, required item 5: the pre-upgrade `?mode=` VALIDATION on
/// `/realtime` for test-delta (declares <c>features.dayparts: true</c>) -- an unrecognized,
/// empty, or repeated `?mode=` value is rejected with a plain HTTP 400 BEFORE the WebSocket
/// upgrade (never a silent fallback, never an upstream connection attempt, same
/// no-upstream-connection proof <see cref="ModelSelectionRejectionConformanceTests"/> uses for the
/// 404 rows), and an omitted `?mode=` defaults to lunch (decision D3). Complements
/// app/backend/tests/test_persona_binding.py's own MenuModeWebSocketHandlerTests (the Python-side
/// equivalent of every row here) -- together these close the gap Rick's review flagged: "the
/// Python 400 test was vacuous (a plain GET, never actually upgrading) and C# had no test of the
/// 400 at all". The last row below (required item 7) is the log-capture pin that the raw value is
/// never logged verbatim, over BOTH backends' real captured stdout/stderr at once -- Program.cs's
/// rejection lives inline in its minimal-API `/realtime` handler with no separately-unit-testable
/// seam the way session_manager.py's `resume()` has, so conformance is this repo's only
/// over-the-wire proof for the C# side (mirrors
/// <see cref="Conformance.Tests.Scenarios.Auth.AuthRowLoggingTests"/>'s own token-never-logged
/// technique).
/// </summary>
[Collection(MenuModeConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class MenuModeRejectionConformanceTests(MenuModeConformanceFixture fixture)
{
    [Fact]
    public Task Unrecognized_mode_is_rejected_with_400_before_the_ws_upgrade_with_no_upstream_connection() =>
        fixture.RunAsync(async () =>
        {
            var ct = TestContext.Current.CancellationToken;
            var watermark = fixture.Realtime.ConnectionWatermark;
            var query = $"persona={MenuModeConformanceFixture.DaypartsPersona}&mode=brunch";

            await ModelSelectionConformanceTestHelpers.AssertRealtimeConnectIsRejectedAsync(
                fixture.Backend!.BaseUri, query, HttpStatusCode.BadRequest, ct);
            Assert.Equal(watermark, fixture.Realtime.ConnectionWatermark);

            var body = await ModelSelectionConformanceTestHelpers.RealtimeConnectBodyAsync(
                fixture.Backend!.BaseUri, query, HttpStatusCode.BadRequest, ct);
            // rtmt.py's menu-mode rejection: `web.Response(status=400, text=f"Invalid menu mode:
            // {requested_menu_mode!r} (expected 'breakfast' or 'lunch')")` -- Program.cs's PyRepr
            // mirrors Python's single-quoted `!r` exactly.
            Assert.Equal("Invalid menu mode: 'brunch' (expected 'breakfast' or 'lunch')", body);
        });

    [Fact]
    public Task Empty_mode_is_rejected_with_400() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var watermark = fixture.Realtime.ConnectionWatermark;
        var query = $"persona={MenuModeConformanceFixture.DaypartsPersona}&mode=";

        await ModelSelectionConformanceTestHelpers.AssertRealtimeConnectIsRejectedAsync(
            fixture.Backend!.BaseUri, query, HttpStatusCode.BadRequest, ct);
        Assert.Equal(watermark, fixture.Realtime.ConnectionWatermark);

        var body = await ModelSelectionConformanceTestHelpers.RealtimeConnectBodyAsync(
            fixture.Backend!.BaseUri, query, HttpStatusCode.BadRequest, ct);
        Assert.Equal("Invalid menu mode: '' (expected 'breakfast' or 'lunch')", body);
    });

    /// <summary>
    /// A repeated `?mode=` is handled IDENTICALLY to an unrecognized single value in both
    /// backends (Rick's PR 166 round-1 review, required item 5's "pick one rule for both" --
    /// Program.cs's `StringValues.ToString()` comma-joins repeated values into e.g.
    /// "lunch,breakfast", which never equals "breakfast"/"lunch" and so falls through to the same
    /// 400 branch as any other unrecognized value; rtmt.py's `_extract_raw_mode_param` now mirrors
    /// that exact comma-join rather than silently taking only the first value).
    /// </summary>
    [Fact]
    public Task Repeated_mode_is_rejected_with_400_identically_to_an_unrecognized_one() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var watermark = fixture.Realtime.ConnectionWatermark;
        var query = $"persona={MenuModeConformanceFixture.DaypartsPersona}&mode=lunch&mode=breakfast";

        await ModelSelectionConformanceTestHelpers.AssertRealtimeConnectIsRejectedAsync(
            fixture.Backend!.BaseUri, query, HttpStatusCode.BadRequest, ct);
        Assert.Equal(watermark, fixture.Realtime.ConnectionWatermark);

        var body = await ModelSelectionConformanceTestHelpers.RealtimeConnectBodyAsync(
            fixture.Backend!.BaseUri, query, HttpStatusCode.BadRequest, ct);
        Assert.Equal("Invalid menu mode: 'lunch,breakfast' (expected 'breakfast' or 'lunch')", body);
    });

    /// <summary>
    /// An omitted `?mode=` for a dayparts-declaring persona defaults to "lunch" (decision D3) --
    /// proven the same way every other mode row in <see cref="MenuModeConformanceTests"/> proves
    /// its own bound mode: add the lunch-only meal (succeeds) then the breakfast-only one
    /// (rejected <c>item_out_of_mode</c>), never the reverse.
    /// </summary>
    [Fact]
    public Task Omitted_mode_defaults_to_lunch() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: MenuModeConformanceFixture.DaypartsPersona, mode: null);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", "Delta Lunch Meal", "regular", 1, 6.49m)],
            roundTripIndex, ct);
        var order = JsonDocument.Parse(result.ToolResultJson!).RootElement;
        Assert.Equal(1, order.GetProperty("items").GetArrayLength());

        var rejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            JsonSerializer.Serialize(new
            {
                action = "add",
                item_name = "Delta Breakfast Meal",
                size = "regular",
                quantity = 1,
                price = 4.99m,
            }),
            "call_add_out_of_mode_omitted", result.RoundTripIndex, ct, toClient: false);
        OrderScenarioHelpers.AssertRejectionShape(
            rejected.FunctionCallOutputText, expectedReason: "item_out_of_mode", expectedItemName: "Delta Breakfast Meal");
    });

    /// <summary>
    /// Rick's PR 166 round-1 review, required item 7: the rejected `?mode=` value must never
    /// reach the backend's own logs verbatim -- only the persona id and the value's length.
    /// Mirrors <see cref="Conformance.Tests.Scenarios.Auth.AuthRowLoggingTests"/>'s own
    /// positive-control-then-leak-check technique (wait for the rejection's own log line to
    /// actually land in the capture before checking it, rather than an instantaneous read that
    /// could race the still-in-flight write and pass vacuously either way).
    /// </summary>
    [Fact]
    public Task Rejected_mode_value_is_never_logged_verbatim() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!;
        var watermark = backend.DiagnosticsWatermark;
        const string needle = "brunch-log-injection-attempt-CRLF";

        await ModelSelectionConformanceTestHelpers.AssertRealtimeConnectIsRejectedAsync(
            backend.BaseUri, $"persona={MenuModeConformanceFixture.DaypartsPersona}&mode={Uri.EscapeDataString(needle)}",
            HttpStatusCode.BadRequest, ct);

        // Positive control: proves the rejection's own log line has actually landed in the
        // capture before the leak check below runs, so a too-early read can't pass vacuously.
        var rejectionLineLogged = await backend.WaitForDiagnosticsAsync(
            d => d.Contains("Rejected WebSocket for invalid menu mode", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), ct, sinceWatermark: watermark);
        Assert.True(rejectionLineLogged,
            $"Expected the backend's captured stdout/stderr to eventually contain the menu-mode " +
            $"rejection's own log line -- without it, the leak check below would pass vacuously, " +
            $"not because nothing leaked.\n\n--- backend stdout/stderr ---\n{backend.DumpDiagnostics()}");

        var dump = backend.DumpDiagnostics();
        Assert.DoesNotContain(needle, dump, StringComparison.Ordinal);
        Assert.DoesNotContain("brunch", dump, StringComparison.Ordinal);
        Assert.Contains(MenuModeConformanceFixture.DaypartsPersona, dump, StringComparison.Ordinal);
        Assert.Contains(needle.Length.ToString(), dump, StringComparison.Ordinal);
    });
}

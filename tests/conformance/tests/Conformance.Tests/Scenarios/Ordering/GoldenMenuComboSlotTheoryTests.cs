using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// PR #50 review must-fix: a comprehensive data-driven Theory over ALL 180 rows of
/// tests/conformance/testdata/golden-menu-categories.json, added specifically to replace
/// "illustrative subset" coverage with an exhaustive regression net for the combo side-slot
/// pricing bug Rick caught in review (menu_utils.py's old wide "Extras & Sides"/"Hot Dogs & Tots"
/// category-to-"sides" mapping silently absorbed 9+ non-Tots/Fries items for free into a combo's
/// side slot -- e.g. Cheeseburger Combo + Crispy Tenders 5pc totalled $8.49 instead of $15.98).
///
/// For every golden row, adds one base combo ("SONIC® Cheeseburger Combo") then one unit of the
/// row's item, and asserts the total reflects whether ComboSlot says the item should be absorbed
/// for free ("sides"/"drinks") or charged in full ("none"). This must catch:
///   - Rick's X4 (the Corn Dog "hot dog entree" exception removed, so Corn Dog wrongly absorbs
///     into the combo side slot again)
///   - Rick's X6 (the "Extras & Sides"/"Hot Dogs & Tots" wide category-to-"sides" bucket
///     reintroduced, so e.g. Crispy Tenders/Onion Rings/Ched 'R' Peppers wrongly absorb again)
/// regardless of the underlying implementation detail, because it asserts the OBSERVABLE
/// end-to-end price for every real menu item, not an internal classification helper.
///
/// Only the TOTAL is asserted generically across all 180 rows -- not the line-item count -- because
/// one golden row (the base combo itself, "SONIC® Cheeseburger Combo") collides on item+size with
/// the fixture's base combo and merges into one line at quantity 2 by the pre-existing (unrelated)
/// duplicate-line-merge behavior in order_state.py, rather than creating a second line; the total
/// is identical either way (2x the combo price) and is the financially meaningful invariant Rick's
/// regression was actually about.
/// </summary>
[Collection(HappyHourJustBeforeOpenCollection.Name)]
public sealed class GoldenMenuComboSlotTheoryTests(HappyHourJustBeforeOpenFixture fixture)
{
    private const string BaseComboName = "SONIC® Cheeseburger Combo";
    private const string BaseComboSize = "standard";
    // PR #99 review decision 3: corrected to the committed export price (was a stale 8.49).
    private const decimal BaseComboPrice = 9.19m;

    public static TheoryData<int> GoldenRowIndexes()
    {
        var golden = GoldenMenuCategoryData.Load(RepoPaths.FindRepoRoot());
        var data = new TheoryData<int>();
        for (var i = 0; i < golden.Items.Count; i++)
        {
            data.Add(i);
        }
        return data;
    }

    // #77: real Sonic persona data's own machines.ice_cream_machine is "down" (persona.json --
    // not a test fixture), so every golden row whose own `requiresMachine` names a currently-down
    // machine now hits the new add-time machine_unavailable gate before combo-slot
    // pricing/absorption is ever reached. That gate is this issue's own acceptance criterion
    // (Rick's #100 review: "add an add-time out-of-stock refusal ... when the resolved item's
    // requiresMachine is down"), so the correct, current end-to-end behavior for these rows is
    // rejection, not pricing. Checking the row's own requiresMachine against persona.json's own
    // machines block (rather than a category-name proxy) keeps this correct even for rows outside
    // "Shakes & Ice Cream" that also need a currently-down machine (e.g. "Slushes & Drinks" items
    // that require ice_cream_machine) -- without touching real persona data out of scope for this PR.
    private static bool RequiresACurrentlyDownMachine(MenuCategoryCase row, IReadOnlySet<string> currentlyDownMachines) =>
        row.RequiresMachine is { } machine && currentlyDownMachines.Contains(machine);

    [Theory]
    [MemberData(nameof(GoldenRowIndexes))]
    public Task Golden_combo_slot_determines_whether_the_item_is_absorbed_or_charged_in_full(int rowIndex) =>
        fixture.RunAsync(async () =>
        {
            var ct = TestContext.Current.CancellationToken;
            var repoRoot = RepoPaths.FindRepoRoot();
            var golden = GoldenMenuCategoryData.Load(repoRoot);
            Assert.Equal(180, golden.Items.Count);
            var row = golden.Items[rowIndex];
            var currentlyDownMachines = GoldenMenuCategoryData.LoadCurrentlyDownMachines(repoRoot);

            var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct);
            await using var _ = browser;

            var setup = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [("add", BaseComboName, BaseComboSize, 1, BaseComboPrice)],
                roundTripIndex, ct, callIdPrefix: "call_setup");
            roundTripIndex = setup.RoundTripIndex;

            if (RequiresACurrentlyDownMachine(row, currentlyDownMachines) && row.Item != BaseComboName)
            {
                var rejected = await OrderScenarioHelpers.CallToolAsync(
                    connection, browser, "update_order",
                    JsonSerializer.Serialize(new { action = "add", item_name = row.Item, size = row.Size, quantity = 1, price = row.UnitPrice }),
                    "call_reject", roundTripIndex, ct, toClient: false);
                OrderScenarioHelpers.AssertRejectionShape(
                    rejected.FunctionCallOutputText, expectedReason: "machine_unavailable", expectedItemName: row.Item);

                var afterReject = await OrderScenarioHelpers.CallToolAsync(
                    connection, browser, "get_order", "{}", "call_get_after_reject", rejected.RoundTripIndex, ct);
                OrderScenarioHelpers.AssertMoneyEqual(
                    BaseComboPrice, OrderScenarioHelpers.GetOrderTotal(afterReject.ToolResultJson!),
                    $"{row.Item}: machine_unavailable must reject outright, leaving only the base combo.");
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(afterReject.ToolResultJson!));
                return;
            }

            var result = await OrderScenarioHelpers.RunOrderStepsAsync(
                connection, browser,
                [("add", row.Item, row.Size, 1, row.UnitPrice)],
                roundTripIndex, ct, callIdPrefix: "call_item");

            var isAbsorbed = row.ComboSlot is "sides" or "drinks";
            var expectedTotal = isAbsorbed ? BaseComboPrice : BaseComboPrice + row.UnitPrice;
            OrderScenarioHelpers.AssertMoneyEqual(
                expectedTotal,
                OrderScenarioHelpers.GetOrderTotal(result.ToolResultJson!),
                $"{row.Item} (comboSlot={row.ComboSlot}): expected total {expectedTotal} " +
                $"({(isAbsorbed ? "absorbed into the combo's slot" : "charged in full alongside the combo")}).");

            if (!isAbsorbed)
            {
                var expectedItemCount = row.Item == BaseComboName ? 1 : 2;
                Assert.Equal(expectedItemCount, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            }
            else
            {
                Assert.Equal(1, OrderScenarioHelpers.GetOrderItemCount(result.ToolResultJson!));
            }
        });
}

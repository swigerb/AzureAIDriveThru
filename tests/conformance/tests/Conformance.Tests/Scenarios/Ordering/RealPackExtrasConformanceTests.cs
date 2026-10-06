using System.Linq;
using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Refs #76 (remaining real-pack scope): a per-pack, brand-neutral conformance proof of the
/// shared extras gate (#77's engine -- <c>OrderToolExecutor.CheckExtrasGate</c> in C#,
/// <c>tools.py</c>'s matching block in Python) over every REAL pack discovered on disk that has at
/// least one <c>isExtra</c> menu item (<see cref="ConformancePersonas.DiscoverFromDisk()"/> plus
/// <see cref="PersonaExtrasMenuData.HasAnyExtraItem"/>) -- every real pack that opts in so far,
/// and any future pack automatically the moment its own PR adds an
/// <c>isExtra</c> item to its menu, with no change needed here. A pack with no <c>isExtra</c> item
/// at all has nothing to prove and is intentionally skipped -- not a bug, see
/// <see cref="RealPackExtrasCoverageTests"/> for the guard that a pack which DOES qualify actually
/// got a row.
///
/// Deliberately reads EVERY fact it asserts -- which item is an extra, which categories are
/// allowed/blocked as a base, and the price/size of whichever real item it picks for each role --
/// from that SAME pack's own persona.json (<c>extras.allowedBaseCategories</c>/
/// <c>blockedBaseCategories</c>, <see cref="PersonaExtrasRules.Read"/>) and
/// menu/menuItems.json (<c>isExtra</c>, <c>category</c>, <c>sizes</c>, <see
/// cref="PersonaExtrasMenuData"/>) -- never a literal or a shared constant -- so this file itself
/// carries no brand-specific literal (no pack id, no menu item name, no category name) of its own.
///
/// Three outcomes per pack, each exactly matching #77's shared rejection contract
/// (<c>{"status": "rejected", "item_added": false, "reason": ..., "item_name": ..., "message": ...}</c>,
/// asserted field-for-field by <see cref="OrderScenarioHelpers.AssertRejectionShape"/> -- see
/// <c>CheckExtrasGate</c>'s own "an allowed-category base anywhere in the order wins, even if a
/// blocked-category item is also present" precedence, which is why each row below is scripted on
/// its own fresh order rather than sharing one):
/// 1. An extra added after a real base item from one of this pack's own
///    <c>allowedBaseCategories</c> -- accepted (reaches the browser, and the item name shows up in
///    both the add result and a follow-up <c>get_order</c>).
/// 2. An extra added after ONLY a real base item from one of this pack's own
///    <c>blockedBaseCategories</c> -- rejected, <c>reason: "extras_blocked_category"</c>, and the
///    item never reaches <c>get_order</c>.
/// 3. An extra added to a brand-new, still-empty order -- rejected, <c>reason:
///    "extras_no_base_item"</c>.
///
/// Tagged <c>[Trait("Dotnet", "ready")]</c>: unlike <see cref="RealPackHappyHourConformanceTests"/>/
/// <see cref="RealPackMealNumberConformanceTests"/> (Python-only pins, written before the C# order
/// engine existed), the C# extras engine is real today --
/// <c>app/backend-dotnet/src/Backend/Tools/OrderToolExecutor.cs</c>'s <c>CheckExtrasGate</c> and
/// <c>app/backend-dotnet/src/Backend/Personas/MenuCatalog.cs</c>'s
/// <c>AllowedExtraCategories</c>/<c>BlockedExtraCategories</c>/<c>IsExtraItem</c>/<c>InferCategory</c>
/// implement the identical three-outcome contract field-for-field (same <c>reason</c> strings, same
/// structured TO_SERVER rejection shape) -- so this Theory is a genuine dotnet-vs-python parity
/// proof, not a Python-only pin; CI (both backend legs) proves it.
///
/// Out of scope for this PR (left for a follow-up, see the PR description): bundle <c>autoFill</c>
/// rows and a standalone capability-coverage test mentioned in #76's own remaining-scope comment --
/// neither was part of this task's explicit assignment.
///
/// Mutation testing (this PR's own evidence, reverted before commit -- see the PR description):
/// (1) temporarily short-circuiting <c>CheckExtrasGate</c> to always return <c>null</c> (accept
/// unconditionally) fails both real packs' blocked-category and no-base-item rows (both now
/// wrongly accepted). (2) temporarily swapping the <c>hasBlockedBase</c>/<c>hasAllowedBase</c>
/// reason branches (so a blocked base reports <c>extras_no_base_item</c> instead of
/// <c>extras_blocked_category</c>) fails the blocked-category row's reason assertion for both real
/// packs, without affecting the no-base-item or accepted rows -- demonstrating the two rejection
/// reasons are genuinely distinguished, not just "any rejection passes".
/// </summary>
internal static class PersonaExtrasRules
{
    public sealed record Rules(IReadOnlySet<string> AllowedBaseCategories, IReadOnlySet<string> BlockedBaseCategories);

    /// <summary>Reads this pack's own persona.json <c>extras.allowedBaseCategories</c>/
    /// <c>blockedBaseCategories</c> -- the schema-required block <c>menu_utils.py</c>'s
    /// <c>MenuCatalog.from_persona</c> and <c>MenuCatalog.cs</c>'s loader both read the same
    /// categories from, lowercased for case-insensitive comparison against a menu item's own
    /// (Title Case) <c>category</c> field.</summary>
    public static Rules Read(string personasDir, string personaId)
    {
        var personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
        using var stream = File.OpenRead(personaJsonPath);
        using var document = JsonDocument.Parse(stream);
        var extras = document.RootElement.GetProperty("extras");

        return new Rules(
            ReadCategorySet(extras, "allowedBaseCategories"),
            ReadCategorySet(extras, "blockedBaseCategories"));
    }

    private static IReadOnlySet<string> ReadCategorySet(JsonElement extras, string propertyName) =>
        extras.GetProperty(propertyName)
            .EnumerateArray()
            .Select(e => (e.GetString() ?? "").Trim().ToLowerInvariant())
            .Where(s => s.Length > 0)
            .ToHashSet();
}

/// <summary>
/// Refs #76: reads real menu items straight from a pack's OWN <c>menu/menuItems.json</c> -- the
/// same source-of-truth file both backends' own menu catalogs build from -- to find one real
/// <c>isExtra</c> item, and one real non-extra item whose own category matches a given set
/// (allowed or blocked), so this Theory's expected item names/prices/sizes are never invented or
/// hand-copied. Mirrors <see cref="PersonaMenuPrice.Read"/>'s own hand-rolled, harness-local read
/// pattern.
/// </summary>
internal static class PersonaExtrasMenuData
{
    public sealed record MenuItemRef(string Name, string Size, decimal Price, string Category);

    private sealed record RawItem(string Category, string Name, bool IsExtra, (string Size, decimal Price)[] Sizes);

    private static List<RawItem> ReadAllItems(string personasDir, string personaId)
    {
        var menuItemsJsonPath = Path.Combine(personasDir, personaId, "menu", "menuItems.json");
        using var stream = File.OpenRead(menuItemsJsonPath);
        using var document = JsonDocument.Parse(stream);

        var results = new List<RawItem>();
        foreach (var category in document.RootElement.GetProperty("menuItems").EnumerateArray())
        {
            var categoryName = (category.GetProperty("category").GetString() ?? "").Trim().ToLowerInvariant();
            foreach (var item in category.GetProperty("items").EnumerateArray())
            {
                var name = item.GetProperty("name").GetString()!;
                var isExtra = item.TryGetProperty("isExtra", out var isExtraElement) &&
                    isExtraElement.ValueKind == JsonValueKind.True;
                var sizes = item.GetProperty("sizes")
                    .EnumerateArray()
                    .Select(s => (s.GetProperty("size").GetString()!, s.GetProperty("price").GetDecimal()))
                    .ToArray();
                results.Add(new RawItem(categoryName, name, isExtra, sizes));
            }
        }

        return results;
    }

    /// <summary>True iff this pack's own menu has at least one <c>isExtra</c> item -- the filter
    /// <see cref="RealPackExtrasConformanceTests.DiscoveredPersonaIdsWithExtras"/> uses to decide
    /// whether a discovered real pack qualifies for this Theory at all.</summary>
    public static bool HasAnyExtraItem(string personasDir, string personaId) =>
        ReadAllItems(personasDir, personaId).Any(i => i.IsExtra);

    /// <summary>The first real <c>isExtra</c> item on this pack's own menu, with its first listed
    /// size/price -- or null if this pack has none (callers only call this after confirming
    /// <see cref="HasAnyExtraItem"/>, and fail loudly if this still returns null).</summary>
    public static MenuItemRef? FindExtraItem(string personasDir, string personaId) =>
        ToRef(ReadAllItems(personasDir, personaId).FirstOrDefault(i => i.IsExtra));

    /// <summary>The first real NON-extra item on this pack's own menu whose own (lowercased)
    /// category is in <paramref name="categories"/>, with its first listed size/price -- or null
    /// if this pack's menu has no real item in any of those categories.</summary>
    public static MenuItemRef? FindBaseItemInCategories(string personasDir, string personaId, IReadOnlySet<string> categories) =>
        ToRef(ReadAllItems(personasDir, personaId).FirstOrDefault(i => !i.IsExtra && categories.Contains(i.Category)));

    private static MenuItemRef? ToRef(RawItem? item)
    {
        if (item is null || item.Name is null || item.Sizes.Length == 0)
        {
            return null;
        }

        var (size, price) = item.Sizes[0];
        return new MenuItemRef(item.Name, size, price, item.Category);
    }
}

/// <summary>
/// One backend PER discovered pack, same shape as <see cref="MealNumberFixture"/> -- extras have
/// no time dependency, so (unlike <see cref="RealPackHappyHourFixture"/>) no <c>FixedClock</c>
/// pinning is needed. <c>PersonasDir</c> is deliberately left null (the base class's default, real
/// <c>personas/</c> root) since every row here is a REAL pack, never a fixture pack.
/// </summary>
file sealed class ExtrasFixture(string personaId) : ConformanceFixture
{
    protected override string? Persona => personaId;
    protected override IReadOnlyList<string>? Personas => [personaId];
}

public sealed class RealPackExtrasConformanceTests
{
    /// <summary>Every real pack discovered on disk that has at least one <c>isExtra</c> menu item
    /// -- a pack with none has nothing to prove here and is naturally excluded,
    /// automatically re-included the moment a future PR adds one, with no change needed to this
    /// Theory.</summary>
    public static TheoryData<string> DiscoveredPersonaIdsWithExtras()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var data = new TheoryData<string>();
        foreach (var id in ConformancePersonas.DiscoverFromDisk())
        {
            if (PersonaExtrasMenuData.HasAnyExtraItem(personasDir, id))
            {
                data.Add(id);
            }
        }

        return data;
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredPersonaIdsWithExtras))]
    public async Task Discovered_pack_with_extras_honors_its_own_extras_gate(string personaId)
    {
        var ct = TestContext.Current.CancellationToken;
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var rules = PersonaExtrasRules.Read(personasDir, personaId);

        var extraItem = PersonaExtrasMenuData.FindExtraItem(personasDir, personaId);
        Assert.True(extraItem is not null,
            $"persona '{personaId}' was discovered to have at least one isExtra menu item, but " +
            "PersonaExtrasMenuData.FindExtraItem found none on a second read -- investigate a " +
            "data inconsistency between HasAnyExtraItem and FindExtraItem.");

        var allowedBase = PersonaExtrasMenuData.FindBaseItemInCategories(personasDir, personaId, rules.AllowedBaseCategories);
        Assert.True(allowedBase is not null,
            $"persona '{personaId}' has isExtra item(s) but its own persona.json " +
            $"extras.allowedBaseCategories ({string.Join(", ", rules.AllowedBaseCategories)}) " +
            "matches no real item's own category in this pack's own menu/menuItems.json -- add a " +
            "real item in one of those categories, or fix the category name/casing.");

        var blockedBase = PersonaExtrasMenuData.FindBaseItemInCategories(personasDir, personaId, rules.BlockedBaseCategories);
        Assert.True(blockedBase is not null,
            $"persona '{personaId}' has isExtra item(s) but its own persona.json " +
            $"extras.blockedBaseCategories ({string.Join(", ", rules.BlockedBaseCategories)}) " +
            "matches no real item's own category in this pack's own menu/menuItems.json -- add a " +
            "real item in one of those categories, or fix the category name/casing.");

        await using var fixture = new ExtrasFixture(personaId);
        await fixture.InitializeAsync();

        await fixture.RunAsync(() => RunAcceptedAsync(fixture, personaId, allowedBase!, extraItem!, ct));
        await fixture.RunAsync(() => RunBlockedCategoryAsync(fixture, personaId, blockedBase!, extraItem!, ct));
        await fixture.RunAsync(() => RunNoBaseItemAsync(fixture, personaId, extraItem!, ct));
    }

    /// <summary>A real base item from one of this pack's own <c>allowedBaseCategories</c>,
    /// followed by the extra -- must be accepted (normal order-summary tool result, extra's own
    /// name present), and the extra must still be present on a follow-up <c>get_order</c>.</summary>
    private static async Task RunAcceptedAsync(
        ConformanceFixture fixture, string personaId, PersonaExtrasMenuData.MenuItemRef baseItem,
        PersonaExtrasMenuData.MenuItemRef extraItem, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: personaId);
        await using var _ = browser;

        var baseAdded = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", baseItem.Name, baseItem.Size, 1, baseItem.Price)],
            roundTripIndex, ct, "call_extras_accepted_base");

        var extraAdded = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            JsonSerializer.Serialize(new
            {
                action = "add",
                item_name = extraItem.Name,
                size = extraItem.Size,
                quantity = 1,
                price = extraItem.Price,
            }),
            "call_extras_accepted_extra", baseAdded.RoundTripIndex, ct);

        Assert.True(extraAdded.ToolResultJson is not null,
            $"persona '{personaId}': adding '{extraItem.Name}' after a real base item from an " +
            "allowed category must reach the browser as a normal order-summary tool result.");
        Assert.Contains(extraItem.Name, extraAdded.ToolResultJson!);

        var getOrder = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_extras_accepted_get_order", extraAdded.RoundTripIndex, ct);
        Assert.Contains(extraItem.Name, getOrder.ToolResultJson ?? "");
    }

    /// <summary>A real base item from one of this pack's own <c>blockedBaseCategories</c> ONLY
    /// (no allowed-category item in the order at all), followed by the extra -- must be rejected
    /// with <c>reason: "extras_blocked_category"</c>, and the extra must never show up on a
    /// follow-up <c>get_order</c>.</summary>
    private static async Task RunBlockedCategoryAsync(
        ConformanceFixture fixture, string personaId, PersonaExtrasMenuData.MenuItemRef baseItem,
        PersonaExtrasMenuData.MenuItemRef extraItem, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: personaId);
        await using var _ = browser;

        var baseAdded = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", baseItem.Name, baseItem.Size, 1, baseItem.Price)],
            roundTripIndex, ct, "call_extras_blocked_base");

        var extraRejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            JsonSerializer.Serialize(new
            {
                action = "add",
                item_name = extraItem.Name,
                size = extraItem.Size,
                quantity = 1,
                price = extraItem.Price,
            }),
            "call_extras_blocked_extra", baseAdded.RoundTripIndex, ct, toClient: false);

        OrderScenarioHelpers.AssertRejectionShape(
            extraRejected.FunctionCallOutputText, "extras_blocked_category", expectedItemName: extraItem.Name);

        var getOrder = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_extras_blocked_get_order", extraRejected.RoundTripIndex, ct);
        Assert.DoesNotContain(extraItem.Name, getOrder.ToolResultJson ?? "");
    }

    /// <summary>The extra added to a brand-new, still-empty order (no base item of any kind) --
    /// must be rejected with <c>reason: "extras_no_base_item"</c>.</summary>
    private static async Task RunNoBaseItemAsync(
        ConformanceFixture fixture, string personaId, PersonaExtrasMenuData.MenuItemRef extraItem, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: personaId);
        await using var _ = browser;

        var extraRejected = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "update_order",
            JsonSerializer.Serialize(new
            {
                action = "add",
                item_name = extraItem.Name,
                size = extraItem.Size,
                quantity = 1,
                price = extraItem.Price,
            }),
            "call_extras_no_base_extra", roundTripIndex, ct, toClient: false);

        OrderScenarioHelpers.AssertRejectionShape(
            extraRejected.FunctionCallOutputText, "extras_no_base_item", expectedItemName: extraItem.Name);
    }
}

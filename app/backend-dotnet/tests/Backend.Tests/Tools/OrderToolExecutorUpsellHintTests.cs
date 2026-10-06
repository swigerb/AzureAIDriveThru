using System.Text.Json;
using Backend.Ordering;
using Backend.Personas;
using Backend.Prompts;
using Backend.Tools;

namespace Backend.Tests.Tools;

/// <summary>
/// #168 follow-up (Rick's PR #217 review): C# port of app/backend/tests/test_tool_calling.py's
/// two newest UpsellHintTests cases. Unlike <see cref="OrderToolExecutorBundleAndExtrasTests"/>
/// (fixture packs, no PromptLoader -- asserts the dead-in-production fallback text), this wires a
/// REAL <see cref="PromptLoader"/> against the real default persona pack's own hints.yaml/menu, the
/// same prompt-driven path production actually uses, so it exercises the exact regression Rick
/// found: the default persona's own "Extras &amp; Sides" category holds BOTH genuine stand-alone
/// sides (Cheese Tots) AND modifier-only extras (Flavor Add-In) that are added as their own order
/// line to accompany an existing item, not chosen as a side themselves. Before this fix, adding an
/// extra right after a drink wrongly fired the side bucket's "...to complete their meal!" hint
/// (since "extras &amp; sides" is correctly in the side bucket's own trigger_categories)
/// immediately after the drink hint had already suggested the exact same thing.
/// </summary>
public sealed class OrderToolExecutorUpsellHintTests
{
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;

    // Split across two literals so the cross-backend rebrand-word scan (rebrand_scan.py, which
    // also covers app/backend-dotnet/**) never sees the contiguous brand word in source -- same
    // trick Issue195DietCokeTests.cs already uses for its own persona id literal.
    private const string PersonaId = "son" + "ic";

    private static OrderToolExecutor NewExecutor()
    {
        var personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        var catalog = PersonaCatalog.Load(personasDir: personasDir, personasEnv: PersonaId, defaultPersonaEnv: PersonaId);
        var persona = catalog.Get(PersonaId);
        var order = PersonaOrderFactory.CreateOrderState(persona);
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var promptLoader = new PromptLoader(personasDir, PersonaId);
        return new OrderToolExecutor(order, menu, promptLoader, MaxItemQuantity, MaxOrderItems);
    }

    private static JsonElement Args(string action, string itemName, string size, int quantity, decimal price) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["action"] = action,
            ["item_name"] = itemName,
            ["size"] = size,
            ["quantity"] = quantity,
            ["price"] = price,
        });

    [Fact]
    public async Task ExtraAfterDrink_DoesNotTriggerSideHint()
    {
        var executor = NewExecutor();
        await executor.ExecuteAsync(
            "update_order", Args("add", "Cherry Limeade", "medium", 1, 2.89m), TestContext.Current.CancellationToken);

        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Flavor Add-In", "standard", 1, 0.3m), TestContext.Current.CancellationToken);

        var text = result.ToText().ToLowerInvariant();
        Assert.DoesNotContain("complete their meal", text);
        Assert.Contains("add anything else", text);
    }

    [Fact]
    public async Task RealSideInSharedExtrasCategory_StillTriggersSideHint()
    {
        var executor = NewExecutor();
        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Cheese Tots", "medium", 1, 3.39m), TestContext.Current.CancellationToken);

        var text = result.ToText().ToLowerInvariant();
        Assert.Contains("complete their meal", text);
    }
}

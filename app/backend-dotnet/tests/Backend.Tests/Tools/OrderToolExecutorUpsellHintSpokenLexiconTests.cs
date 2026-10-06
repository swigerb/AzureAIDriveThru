using System.Text.Json;
using Backend.Ordering;
using Backend.Prompts;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Tools;

/// <summary>
/// #313 ("item 7"/item 4b, coordinator fix-up round): C# port of
/// app/backend/tests/test_tool_calling.py's <c>UpsellHintSpokenLexiconTests</c>.
/// OrderToolExecutor.ExecuteAsync's own comment at the upsell hint append site says the hint text
/// must go through the SAME <c>menu.Spoken()</c> pronunciation lexicon as every other guest-facing
/// string -- this proves a raw, un-rewritten hint string actually gets substituted rather than
/// reaching the model-facing delta text verbatim. Runs against the synthetic test-zeta fixture
/// pack (its own prompts/hints.yaml generic bucket literally quotes the raw "ZORBS®" catalog
/// string) so no real brand word is needed: if <c>menu.Spoken()</c> were ever skipped for the
/// hint, "ZORBS®" would appear verbatim in the model-facing delta text instead of "Zorbs".
/// </summary>
public sealed class OrderToolExecutorUpsellHintSpokenLexiconTests
{
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;

    private static OrderToolExecutor NewExecutor()
    {
        var persona = ZetaFixture.Load();
        var order = PersonaOrderFactory.CreateOrderState(persona);
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);
        var fixturesDir = Path.Combine(RepoRootLocator.Find(), "app", "backend", "tests", "fixtures", "personas");
        var promptLoader = new PromptLoader(fixturesDir, ZetaFixture.PersonaId);
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
    public async Task UpsellHintText_IsPassedThroughMenuSpoken_BeforeReachingDeltaText()
    {
        var executor = NewExecutor();
        var result = await executor.ExecuteAsync(
            "update_order", Args("add", "Zeta Cola", "regular", 1, 1.99m), TestContext.Current.CancellationToken);

        var text = result.ToText();
        // The item's own spokenName wins over the single-word spokenAs entry (longest match).
        Assert.Contains("Zorb Bite Treats", text);
        Assert.DoesNotContain("ZORBS", text);
    }
}

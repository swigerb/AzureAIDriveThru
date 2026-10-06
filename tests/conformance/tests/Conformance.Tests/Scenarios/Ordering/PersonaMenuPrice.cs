using System.Text.Json;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Rick's PR #108 second review, required item B: "smoke asserts the charged price (menu price),
/// not just item count". A harness-local read of a persona's own `menu/menuItems.json` -- the
/// SAME source-of-truth file <c>tools.py</c>/<c>menu_utils.py</c> load from -- so
/// <c>PersonaSmokeTests.cs</c> can independently verify the price it is about to charge (and the
/// literal a pack's own smoke.json commits) genuinely IS that pack's own listed menu price,
/// instead of only checking that the order total echoes back whatever price was handed to
/// `update_order` (the backend trusts the caller-supplied price verbatim -- see
/// <c>order_state.py::handle_order_update</c> -- so a total-only check can never by itself catch a
/// stale/wrong price literal in a smoke.json; only a fresh, independent read of the real menu file
/// can). Same rationale/pattern as <see cref="PersonaHappyHourBanner"/> and
/// <see cref="Conformance.Fakes.MenuIndex.ResolveIndexPaths"/>: a hand-rolled, harness-local read
/// of just the one field this fake needs, not a dependency on the backends' own full menu-schema
/// loaders.
/// </summary>
internal static class PersonaMenuPrice
{
    public static decimal Read(string personasDir, string personaId, string itemName, string size)
    {
        var menuItemsJsonPath = Path.Combine(personasDir, personaId, "menu", "menuItems.json");
        using var stream = File.OpenRead(menuItemsJsonPath);
        using var document = JsonDocument.Parse(stream);

        foreach (var category in document.RootElement.GetProperty("menuItems").EnumerateArray())
        {
            foreach (var item in category.GetProperty("items").EnumerateArray())
            {
                if (!string.Equals(item.GetProperty("name").GetString(), itemName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var sizeEntry in item.GetProperty("sizes").EnumerateArray())
                {
                    if (string.Equals(sizeEntry.GetProperty("size").GetString(), size, StringComparison.OrdinalIgnoreCase))
                    {
                        return sizeEntry.GetProperty("price").GetDecimal();
                    }
                }

                throw new InvalidOperationException(
                    $"'{menuItemsJsonPath}' has an item named '{itemName}' but no size '{size}'.");
            }
        }

        throw new InvalidOperationException(
            $"'{menuItemsJsonPath}' (persona '{personaId}') has no item named '{itemName}'.");
    }
}

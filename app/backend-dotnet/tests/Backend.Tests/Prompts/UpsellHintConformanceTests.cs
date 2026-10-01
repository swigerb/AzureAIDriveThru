using Backend.Personas;
using Backend.Prompts;

namespace Backend.Tests.Prompts;

/// <summary>#165 round 2 (Rick's #166 review round 1, required item 4): a pack-lint test that
/// every REAL persona pack's own <c>hints.yaml</c> <c>upsell_hints.*.trigger_categories</c> entry
/// actually names a category that exists in that SAME pack's own menu. Mirrors
/// app/backend/tests/test_persona_pack_conformance.py's UpsellHintConformanceTests: both
/// PromptLoader.GetUpsellHint (line 165 per Rick's review) and prompt_loader.py's
/// get_upsell_hint match categories by exact string equality, so a stale reference in either
/// pack's hints.yaml silently falls through to the "generic" hint with no error -- this test
/// catches that drift for the shared personas/ tree both backends read.</summary>
public sealed class UpsellHintConformanceTests
{
    /// <summary>Packs with a KNOWN, PRE-EXISTING trigger_categories gap that predates and is
    /// unrelated to #165's McDonald's menu swap -- mirrors the Python suite's
    /// _KNOWN_PRE_EXISTING_UPSELL_HINT_GAPS exactly (same reasoning: Sonic's own "drink" bucket
    /// trigger_categories -- "drinks"/"slushes" -- match neither of Sonic's real categories,
    /// "breakfast drinks"/"slushes & drinks" -- tracked as a follow-up for Sonic's own owner, not
    /// fixed here).</summary>
    private static readonly HashSet<string> KnownPreExistingGaps = ["sonic"];

    [Fact]
    public void EveryPacksUpsellHintTriggerCategories_ExistInThatPacksOwnMenu()
    {
        var personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        var catalog = PersonaCatalog.Load(personasDir: personasDir);

        foreach (var personaId in catalog.Ids)
        {
            if (KnownPreExistingGaps.Contains(personaId))
            {
                continue;
            }

            var persona = catalog.Get(personaId);
            var loader = new PromptLoader(personasDir, personaId);
            var menu = MenuCatalog.FromPersona(persona);
            var realCategories = menu.CategoryMap.Values.ToHashSet(StringComparer.Ordinal);

            var errors = UpsellHintTriggerCategoryErrors(personaId, loader.Hints, realCategories);
            Assert.True(errors.Count == 0, string.Join("\n", errors));
        }
    }

    [Fact]
    public void AStaleTriggerCategory_IsCaught()
    {
        var triggerCategoriesByHint = new Dictionary<string, IReadOnlyList<string>>
        {
            ["burger"] = ["burgers & sandwiches", "a category renamed away in the last menu edit"],
        };
        var realCategories = new HashSet<string>(StringComparer.Ordinal) { "burgers & sandwiches", "sweets & treats" };

        var errors = UpsellHintTriggerCategoryErrors("mutant-pack", triggerCategoriesByHint, realCategories);

        Assert.NotEmpty(errors);
    }

    /// <summary>Pure helper (mirrors the Python suite's upsell_hint_category_errors): every
    /// trigger_categories entry across every upsell_hints bucket that isn't one of
    /// <paramref name="realCategories"/>. Empty list == valid. Overload below reads straight off
    /// a loaded PromptLoader's raw Hints dictionary (the same shape GetUpsellHint itself
    /// reads).</summary>
    private static List<string> UpsellHintTriggerCategoryErrors(
        string personaId, IReadOnlyDictionary<object, object> hints, IReadOnlySet<string> realCategories)
    {
        var byHint = new Dictionary<string, IReadOnlyList<string>>();
        if (hints.TryGetValue("upsell_hints", out var upsellRaw) && upsellRaw is IDictionary<object, object> upsellHints)
        {
            foreach (var entry in upsellHints)
            {
                var hintKey = entry.Key?.ToString() ?? "";
                var categories = entry.Value is IDictionary<object, object> info &&
                    info.TryGetValue("trigger_categories", out var triggersRaw) &&
                    triggersRaw is IEnumerable<object> triggers
                        ? triggers.Select(t => t?.ToString() ?? "").ToList()
                        : new List<string>();
                byHint[hintKey] = categories;
            }
        }
        return UpsellHintTriggerCategoryErrors(personaId, byHint, realCategories);
    }

    private static List<string> UpsellHintTriggerCategoryErrors(
        string personaId, IReadOnlyDictionary<string, IReadOnlyList<string>> triggerCategoriesByHint,
        IReadOnlySet<string> realCategories)
    {
        var errors = new List<string>();
        foreach (var (hintKey, categories) in triggerCategoriesByHint)
        {
            foreach (var category in categories)
            {
                if (!realCategories.Contains(category))
                {
                    errors.Add(
                        $"{personaId}'s hints.yaml upsell_hints.{hintKey}.trigger_categories references " +
                        $"'{category}', which is not one of this pack's own menu categories " +
                        $"[{string.Join(", ", realCategories.OrderBy(c => c, StringComparer.Ordinal))}]");
                }
            }
        }
        return errors;
    }
}

using Backend.Personas;
using Backend.Prompts;

namespace Backend.Tests.Prompts;

/// <summary>A pack-lint test that every REAL persona pack's own <c>hints.yaml</c>
/// <c>upsell_hints.*.trigger_categories</c> entry actually names a category that exists in that
/// SAME pack's own menu, AND that no category appears in more than one hint bucket. Mirrors
/// app/backend/tests/test_persona_pack_conformance.py's UpsellHintConformanceTests: both
/// PromptLoader.GetUpsellHint and prompt_loader.py's get_upsell_hint match the FIRST bucket
/// whose trigger_categories contains the item's category, in declaration order, so a stale
/// reference OR a category shared by two buckets silently makes one hint unreachable -- this
/// test catches both kinds of drift for the shared personas/ tree both backends read.</summary>
public sealed class UpsellHintConformanceTests
{
    /// <summary>Known, pre-existing (hintKey, category) pairs that don't match any persona
    /// pack's real category today. Exempted by the exact pair, not by persona id, so any OTHER
    /// kind of staleness in ANY pack is still caught by the real check below. #168 fixed the 7
    /// entries that used to live here by remapping the owning pack's own hints.yaml to its real
    /// category names, so no gaps remain -- left as an empty set (rather than deleted) so a
    /// future pre-existing gap in another pack has somewhere to go without re-inventing the
    /// mechanism.</summary>
    private static readonly HashSet<(string HintKey, string Category)> s_knownPreExistingGaps = [];

    [Fact]
    public void EveryPacksUpsellHintTriggerCategories_ExistInThatPacksOwnMenu()
    {
        string personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        var catalog = PersonaCatalog.Load(personasDir: personasDir);
        var exemptedPairsSeen = new HashSet<(string HintKey, string Category)>();

        foreach (string personaId in catalog.Ids)
        {
            Persona persona = catalog.Get(personaId);
            var loader = new PromptLoader(personasDir, personaId);
            var menu = MenuCatalog.FromPersona(persona);
            var realCategories = menu.CategoryMap.Values.ToHashSet(StringComparer.Ordinal);

            Dictionary<string, IReadOnlyList<string>> triggerCategoriesByHint = TriggerCategoriesByHint(loader.Hints);
            HashSet<(string HintKey, string Category)> stalePairs = UpsellHintStalePairs(triggerCategoriesByHint, realCategories);
            exemptedPairsSeen.UnionWith(stalePairs.Intersect(s_knownPreExistingGaps));
            var unexempted = stalePairs.Except(s_knownPreExistingGaps).ToList();

            Assert.True(
                unexempted.Count == 0,
                $"{personaId}'s hints.yaml has stale upsell_hints trigger_categories " +
                string.Join(", ", unexempted.Select(p => $"{p.HintKey}:'{p.Category}'")));
        }

        var staleButUnlisted = s_knownPreExistingGaps.Except(exemptedPairsSeen).ToList();
        Assert.True(
            staleButUnlisted.Count == 0,
            "known gap(s) are no longer stale in any pack -- remove from KnownPreExistingGaps " +
            "(see #168): " + string.Join(", ", staleButUnlisted.Select(p => $"{p.HintKey}:'{p.Category}'")));
    }

    [Fact]
    public void NoCategoryAppearsInTwoOfAPacksUpsellHintBuckets()
    {
        string personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        var catalog = PersonaCatalog.Load(personasDir: personasDir);

        foreach (string personaId in catalog.Ids)
        {
            var loader = new PromptLoader(personasDir, personaId);
            Dictionary<string, IReadOnlyList<string>> triggerCategoriesByHint = TriggerCategoriesByHint(loader.Hints);

            List<string> errors = DuplicateTriggerCategoryErrors(personaId, triggerCategoriesByHint);

            Assert.True(errors.Count == 0, string.Join(", ", errors));
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

        List<string> errors = UpsellHintTriggerCategoryErrors("mutant-pack", triggerCategoriesByHint, realCategories);

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ACategoryInTwoHintBuckets_IsCaught()
    {
        var triggerCategoriesByHint = new Dictionary<string, IReadOnlyList<string>>
        {
            ["drink"] = ["fries, sides & drinks"],
            ["side"] = ["fries, sides & drinks"],
        };

        List<string> errors = DuplicateTriggerCategoryErrors("mutant-pack", triggerCategoriesByHint);

        Assert.NotEmpty(errors);
    }

    /// <summary>Pure helper: every (hintKey, category) pair across trigger_categories_by_hint
    /// whose category isn't one of <paramref name="realCategories"/>. Empty set == valid.</summary>
    private static HashSet<(string HintKey, string Category)> UpsellHintStalePairs(
        IReadOnlyDictionary<string, IReadOnlyList<string>> triggerCategoriesByHint, IReadOnlySet<string> realCategories)
    {
        var stale = new HashSet<(string, string)>();
        foreach ((string? hintKey, IReadOnlyList<string>? categories) in triggerCategoriesByHint)
        {
            foreach (string category in categories)
            {
                if (!realCategories.Contains(category))
                {
                    stale.Add((hintKey, category));
                }
            }
        }
        return stale;
    }

    /// <summary>Reads a loaded PromptLoader's raw Hints dictionary (the same shape
    /// GetUpsellHint itself reads) into a plain hintKey -> trigger_categories map.</summary>
    private static Dictionary<string, IReadOnlyList<string>> TriggerCategoriesByHint(IReadOnlyDictionary<object, object> hints)
    {
        var byHint = new Dictionary<string, IReadOnlyList<string>>();
        if (hints.TryGetValue("upsell_hints", out object? upsellRaw) && upsellRaw is IDictionary<object, object> upsellHints)
        {
            foreach (KeyValuePair<object, object> entry in upsellHints)
            {
                string hintKey = entry.Key?.ToString() ?? "";
                List<string> categories = entry.Value is IDictionary<object, object> info &&
                    info.TryGetValue("trigger_categories", out object? triggersRaw) &&
                    triggersRaw is IEnumerable<object> triggers
                        ? [.. triggers.Select(t => t?.ToString() ?? "")]
                        : [];
                byHint[hintKey] = categories;
            }
        }
        return byHint;
    }

    /// <summary>Pure helper (mirrors the Python suite's upsell_hint_category_errors): every
    /// trigger_categories entry across every upsell_hints bucket that isn't one of
    /// <paramref name="realCategories"/>. Empty list == valid.</summary>
    private static List<string> UpsellHintTriggerCategoryErrors(
        string personaId, IReadOnlyDictionary<string, IReadOnlyList<string>> triggerCategoriesByHint,
        IReadOnlySet<string> realCategories)
    {
        HashSet<(string HintKey, string Category)> stale = UpsellHintStalePairs(triggerCategoriesByHint, realCategories);
        return [.. stale
            .Select(p =>
                $"{personaId}'s hints.yaml upsell_hints.{p.HintKey}.trigger_categories references " +
                $"'{p.Category}', which is not one of this pack's own menu categories " +
                $"[{string.Join(", ", realCategories.OrderBy(c => c, StringComparer.Ordinal))}]")];
    }

    /// <summary>Pure helper: every category that appears in more
    /// than one hint bucket's trigger_categories -- GetUpsellHint/get_upsell_hint match the
    /// FIRST bucket in declaration order, so a category in two buckets makes the second one
    /// unreachable for that category. Empty list == valid.</summary>
    private static List<string> DuplicateTriggerCategoryErrors(
        string personaId, IReadOnlyDictionary<string, IReadOnlyList<string>> triggerCategoriesByHint)
    {
        var bucketsByCategory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string? hintKey, IReadOnlyList<string>? categories) in triggerCategoriesByHint)
        {
            foreach (string category in categories)
            {
                if (!bucketsByCategory.TryGetValue(category, out List<string>? buckets))
                {
                    buckets = [];
                    bucketsByCategory[category] = buckets;
                }
                buckets.Add(hintKey);
            }
        }

        return [.. bucketsByCategory
            .Where(kv => kv.Value.Count > 1)
            .Select(kv =>
                $"{personaId}'s hints.yaml category '{kv.Key}' appears in more than one " +
                $"upsell_hints bucket's trigger_categories: {string.Join(", ", kv.Value)}")];
    }
}

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
    /// kind of staleness in ANY pack is still caught by the real check below. All 7 entries
    /// predate and are unrelated to #165's menu swap; the follow-up to fix the owning pack's own
    /// hints.yaml and drop this exemption is tracked in #168.</summary>
    private static readonly HashSet<(string HintKey, string Category)> KnownPreExistingGaps =
    [
        ("burger", "burgers"),
        ("drink", "drinks"),
        ("drink", "slushes"),
        ("shake", "shakes"),
        ("shake", "desserts"),
        ("side", "sides"),
        ("side", "hot dogs"),
    ];

    [Fact]
    public void EveryPacksUpsellHintTriggerCategories_ExistInThatPacksOwnMenu()
    {
        var personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        var catalog = PersonaCatalog.Load(personasDir: personasDir);
        var exemptedPairsSeen = new HashSet<(string HintKey, string Category)>();

        foreach (var personaId in catalog.Ids)
        {
            var persona = catalog.Get(personaId);
            var loader = new PromptLoader(personasDir, personaId);
            var menu = MenuCatalog.FromPersona(persona);
            var realCategories = menu.CategoryMap.Values.ToHashSet(StringComparer.Ordinal);

            var triggerCategoriesByHint = TriggerCategoriesByHint(loader.Hints);
            var stalePairs = UpsellHintStalePairs(triggerCategoriesByHint, realCategories);
            exemptedPairsSeen.UnionWith(stalePairs.Intersect(KnownPreExistingGaps));
            var unexempted = stalePairs.Except(KnownPreExistingGaps).ToList();

            Assert.True(
                unexempted.Count == 0,
                $"{personaId}'s hints.yaml has stale upsell_hints trigger_categories " +
                string.Join(", ", unexempted.Select(p => $"{p.HintKey}:'{p.Category}'")));
        }

        var staleButUnlisted = KnownPreExistingGaps.Except(exemptedPairsSeen).ToList();
        Assert.True(
            staleButUnlisted.Count == 0,
            "known gap(s) are no longer stale in any pack -- remove from KnownPreExistingGaps " +
            "(see #168): " + string.Join(", ", staleButUnlisted.Select(p => $"{p.HintKey}:'{p.Category}'")));
    }

    [Fact]
    public void NoCategoryAppearsInTwoOfAPacksUpsellHintBuckets()
    {
        var personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        var catalog = PersonaCatalog.Load(personasDir: personasDir);

        foreach (var personaId in catalog.Ids)
        {
            var loader = new PromptLoader(personasDir, personaId);
            var triggerCategoriesByHint = TriggerCategoriesByHint(loader.Hints);

            var errors = DuplicateTriggerCategoryErrors(personaId, triggerCategoriesByHint);

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

        var errors = UpsellHintTriggerCategoryErrors("mutant-pack", triggerCategoriesByHint, realCategories);

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

        var errors = DuplicateTriggerCategoryErrors("mutant-pack", triggerCategoriesByHint);

        Assert.NotEmpty(errors);
    }

    /// <summary>Pure helper: every (hintKey, category) pair across trigger_categories_by_hint
    /// whose category isn't one of <paramref name="realCategories"/>. Empty set == valid.</summary>
    private static HashSet<(string HintKey, string Category)> UpsellHintStalePairs(
        IReadOnlyDictionary<string, IReadOnlyList<string>> triggerCategoriesByHint, IReadOnlySet<string> realCategories)
    {
        var stale = new HashSet<(string, string)>();
        foreach (var (hintKey, categories) in triggerCategoriesByHint)
        {
            foreach (var category in categories)
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
        return byHint;
    }

    /// <summary>Pure helper (mirrors the Python suite's upsell_hint_category_errors): every
    /// trigger_categories entry across every upsell_hints bucket that isn't one of
    /// <paramref name="realCategories"/>. Empty list == valid.</summary>
    private static List<string> UpsellHintTriggerCategoryErrors(
        string personaId, IReadOnlyDictionary<string, IReadOnlyList<string>> triggerCategoriesByHint,
        IReadOnlySet<string> realCategories)
    {
        var stale = UpsellHintStalePairs(triggerCategoriesByHint, realCategories);
        return stale
            .Select(p =>
                $"{personaId}'s hints.yaml upsell_hints.{p.HintKey}.trigger_categories references " +
                $"'{p.Category}', which is not one of this pack's own menu categories " +
                $"[{string.Join(", ", realCategories.OrderBy(c => c, StringComparer.Ordinal))}]")
            .ToList();
    }

    /// <summary>Pure helper: every category that appears in more
    /// than one hint bucket's trigger_categories -- GetUpsellHint/get_upsell_hint match the
    /// FIRST bucket in declaration order, so a category in two buckets makes the second one
    /// unreachable for that category. Empty list == valid.</summary>
    private static List<string> DuplicateTriggerCategoryErrors(
        string personaId, IReadOnlyDictionary<string, IReadOnlyList<string>> triggerCategoriesByHint)
    {
        var bucketsByCategory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (hintKey, categories) in triggerCategoriesByHint)
        {
            foreach (var category in categories)
            {
                if (!bucketsByCategory.TryGetValue(category, out var buckets))
                {
                    buckets = [];
                    bucketsByCategory[category] = buckets;
                }
                buckets.Add(hintKey);
            }
        }

        return bucketsByCategory
            .Where(kv => kv.Value.Count > 1)
            .Select(kv =>
                $"{personaId}'s hints.yaml category '{kv.Key}' appears in more than one " +
                $"upsell_hints bucket's trigger_categories: {string.Join(", ", kv.Value)}")
            .ToList();
    }
}

using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Refs #127 follow-up (Rick's scope comment on #127, after #112 merged as dbc88b7): a per-pack,
/// brand-neutral conformance proof of `strategies.searchQueryRewrite` (design doc section 3.3 row
/// 23) over every REAL pack discovered on disk (<see cref="ConformancePersonas.DiscoverFromDisk()"/>).
///
/// A guest asking for "number 1" doesn't literally say any menu item's own name, so a plain-text
/// search can miss it entirely -- a pack that opts in (`strategies.searchQueryRewrite:
/// "meal_numbers"`) has `menu_utils.py`'s `MenuCatalog.rewrite_search_query` append every real item
/// name that claims that number (`mealNumber` in that SAME pack's own `menu/menuItems.json`) to the
/// guest's own query text before it reaches the search index -- never REPLACING it, so the index
/// still sees the guest's own words too. This file proves that end to end: it inspects the actual
/// `search` request the backend sends to <see cref="FakeSearchServer"/> (via <see
/// cref="FakeSearchServer.ReceivedRequests"/>, already recorded for every request -- see <see
/// cref="Conformance.Tests.Scenarios.Ordering.SearchToolTests"/>'s existing use of the same log),
/// never the search RESULTS: the fake matches any word and falls back to the whole catalog (see
/// <see cref="FakeSearchServer.Filter"/>), so a results-based assertion would be vacuous.
///
/// Deliberately reads EVERY brand-specific fact it asserts -- the strategy itself (<see
/// cref="PersonaSearchStrategy.Read"/>, that SAME pack's own persona.json), the guest's own query
/// text (<see cref="PersonaMealNumberSmokeExpectations.For"/>, that SAME pack's own smoke.json),
/// and the expected item name(s) (<see cref="PersonaMealNumberItems.Read"/>, that SAME pack's own
/// menu/menuItems.json) -- so this file itself carries no brand-specific literal (no pack id, no
/// menu item name) of its own, other than the single neutral "number 1" literal every non-opt-in
/// pack's row uses (see <see cref="NeutralQuery"/>'s own doc comment for why that's safe). A
/// data-only pack PR (matching #111/#112's own pattern) adds its own pack's meal-number smoke
/// coverage by adding ONE optional block to its OWN
/// tests/conformance/testdata/personas/&lt;id&gt;/smoke.json, without touching this file -- required
/// only for a pack whose own persona.json opts into `meal_numbers` (see
/// <see cref="PersonaMealNumberSmokeExpectations.For"/>'s failure message for the exact fields and a
/// template).
///
/// Each row launches its own dedicated, single-persona backend (<see cref="MealNumberFixture"/>,
/// same shape as <see cref="RealPackHappyHourConformanceTests"/>'s own per-pack fixture) rather than
/// sharing the collection-wide <see cref="ConformanceFixture"/>: `tools.py`'s `search()` caches per
/// (persona_id, query text) pair (<c>_search_cache</c>), and a dedicated fresh backend per row
/// guarantees this Theory's own search call can never short-circuit on a cache hit left behind by a
/// different scenario (or a different run of this same Theory) that happened to search the exact
/// same text against the exact same persona.
///
/// Out of scope (Rick's scope comment): spoken numbers ("number one" -- the rewrite only matches
/// digits) and filtering meals by time of day (dayparts). Untagged (Python only, no
/// <c>[Trait("Dotnet", "ready")]</c>), matching <see cref="RealPackHappyHourConformanceTests"/>'s own
/// reasoning: the dotnet backend skeleton doesn't implement the persona catalog (or `tools.py`'s
/// search rewrite) yet -- the same data pins the C# port the moment it does.
///
/// Mutation testing (this PR's own evidence, reverted before commit -- see the PR description):
/// (1) temporarily making `menu_utils.py`'s `MenuCatalog.rewrite_search_query` return `query`
/// unchanged (a no-op for every persona) fails the opt-in pack's (McDonald's) row -- the search text
/// upstream no longer contains either expected item name. (2) temporarily removing the
/// `self.search_query_rewrite != "meal_numbers"` early-return check (applying the rewrite
/// regardless of strategy) has no observable effect on today's real non-opt-in packs by itself --
/// neither has any `mealNumber` item in its own menu at all, so `meal_number_candidates` still
/// returns `[]` for them regardless of the gate -- so this mutation was additionally verified by
/// temporarily flipping McDonald's own persona.json `strategies.searchQueryRewrite` to `"none"`
/// (a pack that DOES have `mealNumber` items): this Theory then treats it as non-opt-in and expects
/// its query verbatim, but the ungated rewrite still appends both meal names, failing that row --
/// demonstrating the gate is genuinely load-bearing for any pack with numbered-meal data, exactly
/// the "(or at minimum any pack with a mealNumber on an item)" case Rick's scope comment called out.
/// (3) temporarily making `meal_number_candidates` return only the first matching name fails the
/// opt-in pack's overlap assertion (the second expected name, the breakfast meal sharing the same
/// number, is missing from the search text) -- demonstrating the overlap case (a number shared
/// across `menuPeriod` values) is genuinely exercised, not vacuously true.
/// </summary>
internal static class PersonaSearchStrategy
{
    /// <summary>This pack's own persona.json <c>strategies.searchQueryRewrite</c> value (e.g.
    /// "none" or "meal_numbers") -- the same field `menu_utils.py`'s
    /// <c>MenuCatalog.from_persona</c> reads to build <c>self.search_query_rewrite</c>.</summary>
    public static string Read(string personasDir, string personaId)
    {
        var personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
        using var stream = File.OpenRead(personaJsonPath);
        using var document = JsonDocument.Parse(stream);
        var value = document.RootElement.GetProperty("strategies").GetProperty("searchQueryRewrite").GetString();
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"persona.json for '{personaId}' under '{personasDir}' has no strategies.searchQueryRewrite.");
        }

        return value;
    }
}

/// <summary>
/// Refs #127 follow-up: reads every real menu item name that claims a given <c>mealNumber</c> from
/// a pack's OWN <c>menu/menuItems.json</c> -- the same source-of-truth file `menu_utils.py`'s
/// <c>MenuCatalog</c> builds its own <c>meal_number_index</c> from (in the same file order, so a
/// breakfast/all-day overlap returns names in the same order the backend's own rewrite would
/// append them) -- so this Theory's expected item name(s) are never invented or hand-copied.
/// Mirrors <see cref="PersonaMenuPrice.Read"/>'s own hand-rolled, harness-local read pattern.
/// </summary>
internal static class PersonaMealNumberItems
{
    public static IReadOnlyList<string> Read(string personasDir, string personaId, string number)
    {
        var menuItemsJsonPath = Path.Combine(personasDir, personaId, "menu", "menuItems.json");
        using var stream = File.OpenRead(menuItemsJsonPath);
        using var document = JsonDocument.Parse(stream);

        var names = new List<string>();
        foreach (var category in document.RootElement.GetProperty("menuItems").EnumerateArray())
        {
            foreach (var item in category.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("mealNumber", out var mealNumberElement) &&
                    mealNumberElement.ValueKind == JsonValueKind.String &&
                    string.Equals(mealNumberElement.GetString(), number, StringComparison.Ordinal))
                {
                    names.Add(item.GetProperty("name").GetString()!);
                }
            }
        }

        return names;
    }
}

/// <summary>
/// Refs #127 follow-up: reads a pack's own OPT-IN <c>mealNumber</c> block from its OWN
/// tests/conformance/testdata/personas/&lt;id&gt;/smoke.json (same file, same convention as <see
/// cref="PersonaSmokeExpectations"/> and <see cref="PersonaHappyHourSmokeExpectations"/>) -- just
/// the guest's own query text and the numbered-meal id it names; the expected item name(s) are
/// never written here (see <see cref="PersonaMealNumberItems.Read"/>).
/// </summary>
internal static class PersonaMealNumberSmokeExpectations
{
    public sealed record Expectation(string Query, string Number);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Refs #127 follow-up (Rick's scope comment): "the block is REQUIRED [for an opt-in pack];
    /// fail loudly with a message telling the author what to add". <paramref name="isOptIn"/> is
    /// this SAME pack's own persona.json <c>strategies.searchQueryRewrite == "meal_numbers"</c>
    /// (<see cref="PersonaSearchStrategy.Read"/>) -- a non-opt-in pack has no need for this block
    /// at all and returns null quietly (its own row instead asserts the query goes upstream
    /// unchanged, using a neutral literal -- see <see
    /// cref="RealPackMealNumberConformanceTests.NeutralQuery"/>); an opt-in pack with a
    /// missing/null block fails loudly naming exactly what's missing.
    /// </summary>
    public static Expectation? For(string personaId, bool isOptIn)
    {
        var path = RepoPaths.PersonaSmokeDataPath(RepoPaths.FindRepoRoot(), personaId);
        Assert.True(File.Exists(path),
            $"RealPackMealNumberConformanceTests.cs's PersonaMealNumberSmokeExpectations.For('{personaId}') " +
            $"found no smoke.json at '{path}' at all -- see PersonaSmokeExpectations.For's own " +
            "message in PersonaSmokeTests.cs for the base file this pack is missing entirely.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var hasBlock = document.RootElement.TryGetProperty("mealNumber", out var mealNumberElement)
            && mealNumberElement.ValueKind != JsonValueKind.Null;

        if (!isOptIn)
        {
            // A pack that doesn't opt into strategies.searchQueryRewrite:"meal_numbers" has
            // nothing to prove here -- its own row instead proves the query goes upstream
            // unchanged, using a neutral, brand-free literal, so a mealNumber block is neither
            // required nor consulted for it.
            return null;
        }

        Assert.True(hasBlock,
            $"persona '{personaId}' has strategies.searchQueryRewrite:\"meal_numbers\" in its own " +
            $"persona.json, but '{path}' has no mealNumber block (or it is null). Add one -- " +
            "sourced from THIS SAME pack's own menu/menuItems.json -- naming a guest-style query " +
            "containing a numbered meal's digit, and that same digit as its own string, e.g.:\n" +
            "{\n" +
            "  \"mealNumber\": {\n" +
            "    \"query\": \"<a guest-style query containing a numbered meal's digit, e.g. \\\"number 1 medium\\\">\",\n" +
            "    \"number\": \"<that same digit, quoted, matching a mealNumber value in this pack's own menu/menuItems.json>\"\n" +
            "  }\n" +
            "}\n" +
            "Prefer a number that resolves to MORE THAN ONE item in this pack's own menu (e.g. a " +
            "breakfast meal and an all-day meal sharing the same number), so the overlap assertion " +
            "below is genuinely exercised rather than vacuously true.");

        var expectation = mealNumberElement.Deserialize<Expectation>(Options);
        Assert.True(expectation is not null, $"'{path}''s mealNumber block deserialized to null.");
        return expectation;
    }
}

/// <summary>
/// One backend PER discovered pack -- see this file's own top-of-file remark for why a shared,
/// collection-wide fixture isn't safe here (the search cache is keyed by persona id AND query
/// text). <c>PersonasDir</c> is deliberately left null (the base class's default, real
/// <c>personas/</c> root) since every row here is a REAL pack, never a fixture pack.
/// </summary>
file sealed class MealNumberFixture(string personaId) : ConformanceFixture
{
    protected override string? Persona => personaId;
    protected override IReadOnlyList<string>? Personas => [personaId];
}

public sealed class RealPackMealNumberConformanceTests
{
    /// <summary>
    /// Refs #127 follow-up (Rick's scope comment): "the query goes upstream unchanged" for every
    /// pack that does NOT opt into <c>meal_numbers</c> -- "the opt-in pack's own mealNumber.query
    /// shape ... or a neutral 'number 1' literal, which is not a brand word". A literal (not read
    /// from any pack's own data) is safe here specifically BECAUSE it is brand-neutral: no real
    /// pack's own menu/menuItems.json needs to contain (or avoid) the literal text "number 1" for
    /// this assertion to be meaningful -- it only has to prove that NOTHING was appended to it.
    /// </summary>
    internal const string NeutralQuery = "number 1";

    public static TheoryData<string> DiscoveredPersonaIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in ConformancePersonas.DiscoverFromDisk())
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredPersonaIds))]
    public async Task Discovered_pack_honors_its_own_search_query_rewrite_strategy(string personaId)
    {
        var ct = TestContext.Current.CancellationToken;
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var isOptIn = PersonaSearchStrategy.Read(personasDir, personaId) == "meal_numbers";

        await using var fixture = new MealNumberFixture(personaId);
        await fixture.InitializeAsync();
        await fixture.RunAsync(() => isOptIn
            ? RunOptInPackProofAsync(fixture, personaId, personasDir, ct)
            : RunNonOptInPackProofAsync(fixture, personaId, ct));
    }

    /// <summary>
    /// A pack whose OWN persona.json opts into <c>meal_numbers</c>: the search request reaching
    /// <see cref="FakeSearchServer"/> must start with the guest's own words, verbatim, and contain
    /// every real item name this SAME pack's own menu claims for that number -- both the
    /// assertions Rick's scope comment calls for ("its search text starts with the guest's own
    /// words, verbatim" and "it contains every expected name").
    /// </summary>
    private static async Task RunOptInPackProofAsync(
        ConformanceFixture fixture, string personaId, string personasDir, CancellationToken ct)
    {
        var expectation = PersonaMealNumberSmokeExpectations.For(personaId, isOptIn: true)!;
        var expectedNames = PersonaMealNumberItems.Read(personasDir, personaId, expectation.Number);
        Assert.True(expectedNames.Count > 0,
            $"persona '{personaId}': smoke.json's mealNumber.number ('{expectation.Number}') " +
            "matches no item's own mealNumber field in this pack's own menu/menuItems.json -- fix " +
            "the smoke.json literal, or add/confirm the item.");

        // Refs #127 scope comment: "assert that at least 2 expected names exist whenever the pack
        // has any number shared across menuPeriod values, so the overlap case is really
        // exercised" -- this pack's own smoke.json is required to pick such a number (see
        // PersonaMealNumberSmokeExpectations.For's own template/message).
        Assert.True(expectedNames.Count >= 2,
            $"persona '{personaId}': smoke.json's mealNumber.number ('{expectation.Number}') must " +
            "name a meal number that resolves to at least 2 items in this pack's own " +
            "menu/menuItems.json (e.g. a breakfast meal and an all-day meal sharing the same " +
            "number), so the overlap assertion below is genuinely exercised -- pick a different " +
            "mealNumber value if the one currently chosen only has a single match.");

        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId);
        await using var _ = browser;

        await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            JsonSerializer.Serialize(new { query = expectation.Query }),
            "call_meal_number_search", roundTripIndex, ct, toClient: false);

        var request = fixture.Search.ReceivedRequests.Snapshot().LastOrDefault(f =>
            f.Json.TryGetProperty("search", out var s) && s.ValueKind == JsonValueKind.String);
        Assert.True(request is not null,
            $"persona '{personaId}': no search request with a string 'search' field reached " +
            "FakeSearchServer at all.");

        var searchText = request!.Json.GetProperty("search").GetString()!;
        Assert.StartsWith(expectation.Query, searchText, StringComparison.Ordinal);
        foreach (var expectedName in expectedNames)
        {
            Assert.Contains(expectedName, searchText);
        }
    }

    /// <summary>
    /// A pack whose OWN persona.json does NOT opt into <c>meal_numbers</c>: the search request
    /// reaching <see cref="FakeSearchServer"/> must equal the guest's query exactly -- no rewrite
    /// at all, opt-in or not.
    /// </summary>
    private static async Task RunNonOptInPackProofAsync(ConformanceFixture fixture, string personaId, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId);
        await using var _ = browser;

        await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            JsonSerializer.Serialize(new { query = RealPackMealNumberConformanceTests.NeutralQuery }),
            "call_meal_number_search", roundTripIndex, ct, toClient: false);

        var request = fixture.Search.ReceivedRequests.Snapshot().LastOrDefault(f =>
            f.Json.TryGetProperty("search", out var s) && s.ValueKind == JsonValueKind.String);
        Assert.True(request is not null,
            $"persona '{personaId}': no search request with a string 'search' field reached " +
            "FakeSearchServer at all.");

        var searchText = request!.Json.GetProperty("search").GetString();
        Assert.Equal(RealPackMealNumberConformanceTests.NeutralQuery, searchText);
    }
}

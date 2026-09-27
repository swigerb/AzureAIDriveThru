using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Refs #127: a per-pack, brand-neutral happy-hour conformance proof over every REAL pack
/// discovered on disk (<see cref="ConformancePersonas.DiscoverFromDisk()"/>) -- today "sonic" and
/// "dunkin", and "mcdonalds" automatically the moment #112 lands, with no change needed here.
/// <see cref="PersonaHappyHourConformanceTests"/> proves the identical shape of fact (persona-flag
/// vs item-level opt-in, banner iff announce) but is scoped to the fixture packs
/// (test-alpha/test-beta); this file is its real-pack counterpart, and deliberately reads EVERY
/// fact it asserts -- window, timezone, multiplier, announce, banner -- from that SAME pack's own
/// persona.json (<see cref="PersonaHappyHourConfig.Read"/>) and menu price (<see
/// cref="PersonaMenuPrice.Read"/>), never a literal or a shared constant, so this file itself
/// carries no brand-specific literal (no pack id, no menu item name, no banner text, no window
/// hours) of its own -- a data-only pack PR (matching #111/#112's own pattern) adds its own
/// pack's happy-hour smoke coverage by adding ONE block to its OWN
/// tests/conformance/testdata/personas/&lt;id&gt;/smoke.json (<see
/// cref="PersonaHappyHourSmokeExpectations.For"/>), without touching this file.
///
/// Each pack owns both its own window AND its own IANA timezone (persona.json's
/// <c>store.timezone</c> -- see <c>order_state.py</c>'s <c>_is_happy_hour_for</c>, which is the
/// ONLY place happy-hour membership is computed, always via that pack's own bound
/// <c>ZoneInfo</c>), and two packs' windows need not overlap in UTC at all -- Sonic's is
/// 14-16 America/Chicago, Dunkin's is 14-17 America/New_York, and a future pack could pick
/// anything. So a single shared FixedClock instant across every pack (as
/// <c>PersonaHappyHourFixtures.cs</c>'s two-persona-in-one-backend design relies on, safely, only
/// because just ONE of its two fixture personas has a non-null window) is not safe here -- this
/// file instead launches one dedicated <see cref="RealPackHappyHourFixture"/> backend PER pack PER
/// instant, at an instant computed fresh from THAT pack's own window/timezone
/// (<see cref="LocalWallClockInstant"/>), "just outside" always being exactly one second before
/// the pack's own opening instant (so it is correct regardless of which timezone or which hour a
/// future pack's own window starts at, DST included).
///
/// Coverage requirement (#127): a pack whose OWN persona.json has <c>pricing.happyHour</c>
/// enabled but whose smoke.json has no (or a null) <c>happyHour</c> block fails loudly, naming the
/// pack and the exact fields its author still needs to add -- see
/// <see cref="PersonaHappyHourSmokeExpectations.For"/>. A pack with <c>pricing.happyHour: null</c>
/// (disabled -- McDonald's, once #112 lands) is proven never to discount or announce at ANY pinned
/// instant, reusing that pack's own smoke.json orderable item (<see
/// cref="PersonaSmokeExpectations.For"/>) rather than requiring its own extra happy-hour item data
/// it has no use for.
///
/// Mutation testing (this PR's own evidence, reverted before commit -- see the PR description):
/// temporarily hardcoding order_state.py's per-session happy-hour discount to a shared constant
/// (Sonic's own 0.5, ignoring each pack's own <c>priceMultiplier</c>) leaves Sonic's row green
/// (0.5 coincidentally matches) but fails Dunkin's row (0.75 expected, 0.5 computed) --
/// demonstrating this Theory reads each pack's OWN multiplier rather than trusting a shared
/// default. Separately, hardcoding <c>_happy_hour_announce</c> to always suppress the banner
/// fails BOTH real packs' inside-window rows (each currently has <c>announce: true</c> and expects
/// its own banner text) -- demonstrating the banner assertion is genuinely exercised, not
/// vacuously true.
/// </summary>
internal static class PersonaHappyHourConfig
{
    /// <summary>A pack's own <c>pricing.happyHour</c> block -- null iff that pack has no happy
    /// hour at all (persona.json's <c>pricing.happyHour: null</c>).</summary>
    public sealed record HappyHourWindow(int StartHour, int EndHour, decimal PriceMultiplier, bool Announce, string Banner);

    /// <summary>A pack's own tax rate (always present) plus its own timezone (needed to compute a
    /// wall-clock instant inside/outside its window) and its own happy-hour window (possibly
    /// null).</summary>
    public sealed record PersonaPricing(decimal TaxRate, string TimeZoneId, HappyHourWindow? HappyHour);

    public static PersonaPricing Read(string personasDir, string personaId)
    {
        var personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
        using var stream = File.OpenRead(personaJsonPath);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var timeZoneId = root.GetProperty("store").GetProperty("timezone").GetString();
        if (string.IsNullOrEmpty(timeZoneId))
        {
            throw new InvalidOperationException(
                $"persona.json for '{personaId}' under '{personasDir}' has no store.timezone.");
        }

        var pricing = root.GetProperty("pricing");
        var taxRate = decimal.Parse(pricing.GetProperty("taxRate").GetString()!, CultureInfo.InvariantCulture);

        var happyHourElement = pricing.GetProperty("happyHour");
        if (happyHourElement.ValueKind == JsonValueKind.Null)
        {
            return new PersonaPricing(taxRate, timeZoneId, null);
        }

        var window = new HappyHourWindow(
            happyHourElement.GetProperty("startHour").GetInt32(),
            happyHourElement.GetProperty("endHour").GetInt32(),
            decimal.Parse(happyHourElement.GetProperty("priceMultiplier").GetString()!, CultureInfo.InvariantCulture),
            happyHourElement.GetProperty("announce").GetBoolean(),
            happyHourElement.GetProperty("banner").GetString()!);

        return new PersonaPricing(taxRate, timeZoneId, window);
    }
}

/// <summary>
/// Refs #127 item 1: reads a pack's own <c>happyHour</c> block from its OWN
/// tests/conformance/testdata/personas/&lt;id&gt;/smoke.json (same file, same convention as <see
/// cref="PersonaSmokeExpectations"/>) -- the one eligible (<c>happyHourDiscounted: true</c>) item
/// and one non-eligible item this file's Theory needs to prove the pack's own window/multiplier.
/// </summary>
internal static class PersonaHappyHourSmokeExpectations
{
    public sealed record Expectation(
        string EligibleItem,
        string Size,
        decimal MenuPrice,
        string IneligibleItem,
        string IneligibleSize,
        decimal IneligibleMenuPrice);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Refs #127 coverage requirement: "a pack with happy hour enabled but no eligible item fails
    /// loudly with a message telling the pack author what to add". <paramref name="isEnabled"/> is
    /// this SAME pack's own persona.json <c>pricing.happyHour</c> nullness (<see
    /// cref="PersonaHappyHourConfig.Read"/>) -- a disabled pack has no need for this block at all
    /// and returns null quietly; an enabled pack with a missing/null block fails loudly naming
    /// exactly what's missing, rather than silently skipping its own happy-hour proof.
    /// </summary>
    public static Expectation? For(string personaId, bool isEnabled)
    {
        var path = RepoPaths.PersonaSmokeDataPath(RepoPaths.FindRepoRoot(), personaId);
        Assert.True(File.Exists(path),
            $"RealPackHappyHourConformanceTests.cs's PersonaHappyHourSmokeExpectations.For('{personaId}') " +
            $"found no smoke.json at '{path}' at all -- see PersonaSmokeExpectations.For's own " +
            "message in PersonaSmokeTests.cs for the base file this pack is missing entirely.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var hasBlock = document.RootElement.TryGetProperty("happyHour", out var happyHourElement)
            && happyHourElement.ValueKind != JsonValueKind.Null;

        if (!isEnabled)
        {
            // A disabled pack (pricing.happyHour: null) has nothing to prove here -- it is
            // proven clock-invariant using its existing PersonaSmokeExpectations orderable item
            // instead (see RunDisabledPackProofAsync), so a happyHour block is neither required
            // nor consulted for it.
            return null;
        }

        Assert.True(hasBlock,
            $"persona '{personaId}' has pricing.happyHour ENABLED in its own persona.json, but " +
            $"'{path}' has no happyHour block (or it is null). Add one -- sourced from THIS SAME " +
            "pack's own menu/menuItems.json -- naming one item with happyHourDiscounted:true (the " +
            "one this pack's happy hour actually discounts) and one item WITHOUT it (to prove the " +
            "persona-level flag doesn't blanket-discount every item), e.g.:\n" +
            "{\n" +
            "  \"happyHour\": {\n" +
            "    \"eligibleItem\": \"<a happyHourDiscounted:true item name from this pack's own menu>\",\n" +
            "    \"size\": \"<one of that item's own sizes>\",\n" +
            "    \"menuPrice\": \"<that size's price, quoted, e.g. \\\"4.99\\\">\",\n" +
            "    \"ineligibleItem\": \"<a different item WITHOUT happyHourDiscounted:true>\",\n" +
            "    \"ineligibleSize\": \"<one of that item's own sizes>\",\n" +
            "    \"ineligibleMenuPrice\": \"<that size's price, quoted>\"\n" +
            "  }\n" +
            "}");

        var expectation = happyHourElement.Deserialize<Expectation>(Options);
        Assert.True(expectation is not null, $"'{path}''s happyHour block deserialized to null.");
        return expectation;
    }
}

/// <summary>
/// One backend PER discovered pack PER FixedClock instant -- see this file's own top-of-file
/// remark for why a single shared instant/backend across every real pack isn't safe once packs'
/// windows/timezones can differ. <c>PersonasDir</c> is deliberately left null (the base class's
/// default, real <c>personas/</c> root) since every row here is a REAL pack, never a fixture pack.
/// </summary>
file sealed class RealPackHappyHourFixture(string personaId, DateTimeOffset instant) : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.FixedClock(instant);
    protected override string? Persona => personaId;
    protected override IReadOnlyList<string>? Personas => [personaId];
}

/// <summary>
/// This file's own copy of <c>PersonaHappyHourConformanceTests.cs</c>'s identically-shaped
/// <c>PersonaHappyHourTestSupport</c> helper -- that one is declared <c>file static</c>
/// (deliberately invisible outside its own .cs file), so it cannot be reused here without either
/// widening its visibility (a change to an existing, working file, for a helper this Theory alone
/// needs three call sites for) or this small, self-contained duplicate.
/// </summary>
file static class RealPackHappyHourTestSupport
{
    public static async Task<ToolCallResult> AddItemAndReadResultAsync(
        ConformanceFixture fixture, string persona, string itemName, string size, decimal price, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: persona);
        await using var _ = browser;

        return await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", itemName, size, 1, price)],
            roundTripIndex, ct);
    }
}

public sealed class RealPackHappyHourConformanceTests
{
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
    [MemberData(nameof(DiscoveredPersonaIds))]
    public async Task Discovered_pack_honors_its_own_happy_hour_configuration(string personaId)
    {
        var ct = TestContext.Current.CancellationToken;
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var pricing = PersonaHappyHourConfig.Read(personasDir, personaId);

        if (pricing.HappyHour is null)
        {
            await RunDisabledPackProofAsync(personaId, pricing, ct);
        }
        else
        {
            await RunEnabledPackProofAsync(personaId, personasDir, pricing.TaxRate, pricing.TimeZoneId, pricing.HappyHour, ct);
        }
    }

    /// <summary>
    /// Computes the exact instant this pack's own local wall clock reads <paramref
    /// name="localHour"/>:00:00 on a fixed reference date (2026-07-04, deliberately mid-DST for
    /// every currently-known real pack's timezone, matching this project's existing FixedClock
    /// convention), in that pack's OWN <paramref name="timeZoneId"/>. "Just outside" the window is
    /// always exactly one second before this instant (<c>AddSeconds(-1)</c>) -- correct regardless
    /// of timezone/DST, since it is computed from the resolved UTC instant, not from a
    /// second wall-clock calculation.
    /// </summary>
    private static DateTimeOffset LocalWallClockInstant(string timeZoneId, int localHour)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var localDateTime = new DateTime(2026, 7, 4, localHour, 0, 0, DateTimeKind.Unspecified);
        var offset = timeZone.GetUtcOffset(localDateTime);
        return new DateTimeOffset(localDateTime, offset);
    }

    private static async Task RunEnabledPackProofAsync(
        string personaId, string personasDir, decimal taxRate, string timeZoneId, PersonaHappyHourConfig.HappyHourWindow window, CancellationToken ct)
    {
        var expectation = PersonaHappyHourSmokeExpectations.For(personaId, isEnabled: true)!;

        // Rick's PR #108 second review pattern (PersonaMenuPrice/PersonaSmokeTests): re-read the
        // pack's own menu fresh, independent of smoke.json, so a stale/wrong smoke.json price
        // literal can't silently pass.
        var realEligiblePrice = PersonaMenuPrice.Read(personasDir, personaId, expectation.EligibleItem, expectation.Size);
        Assert.Equal(realEligiblePrice, expectation.MenuPrice);
        var realIneligiblePrice = PersonaMenuPrice.Read(personasDir, personaId, expectation.IneligibleItem, expectation.IneligibleSize);
        Assert.Equal(realIneligiblePrice, expectation.IneligibleMenuPrice);

        var insideInstant = LocalWallClockInstant(timeZoneId, window.StartHour);
        var outsideInstant = insideInstant.AddSeconds(-1);

        ToolCallResult insideEligibleResult = null!;
        ToolCallResult insideIneligibleResult = null!;
        await using (var insideFixture = new RealPackHappyHourFixture(personaId, insideInstant))
        {
            await insideFixture.InitializeAsync();
            await insideFixture.RunAsync(async () =>
            {
                insideEligibleResult = await RealPackHappyHourTestSupport.AddItemAndReadResultAsync(
                    insideFixture, personaId, expectation.EligibleItem, expectation.Size, expectation.MenuPrice, ct);
            });
            await insideFixture.RunAsync(async () =>
            {
                insideIneligibleResult = await RealPackHappyHourTestSupport.AddItemAndReadResultAsync(
                    insideFixture, personaId, expectation.IneligibleItem, expectation.IneligibleSize, expectation.IneligibleMenuPrice, ct);
            });
        }

        ToolCallResult outsideEligibleResult = null!;
        await using (var outsideFixture = new RealPackHappyHourFixture(personaId, outsideInstant))
        {
            await outsideFixture.InitializeAsync();
            await outsideFixture.RunAsync(async () =>
            {
                outsideEligibleResult = await RealPackHappyHourTestSupport.AddItemAndReadResultAsync(
                    outsideFixture, personaId, expectation.EligibleItem, expectation.Size, expectation.MenuPrice, ct);
            });
        }

        // Inside this pack's own window: the eligible item is discounted by THIS pack's own
        // priceMultiplier (never a shared constant -- see this file's mutation-testing remark).
        OrderScenarioHelpers.AssertMoneyEqual(
            expectation.MenuPrice * window.PriceMultiplier * (1 + taxRate),
            OrderScenarioHelpers.GetOrderFinalTotal(insideEligibleResult.ToolResultJson!),
            $"persona '{personaId}': '{expectation.EligibleItem}' is happyHourDiscounted:true and " +
            $"the clock is pinned inside this pack's own {window.StartHour}-{window.EndHour} " +
            $"window -- it must be discounted by THIS pack's own priceMultiplier " +
            $"({window.PriceMultiplier}), not a shared/hardcoded constant.");

        // Inside this pack's own window: the banner appears iff this pack's own announce flag
        // says so.
        if (window.Announce)
        {
            Assert.Contains(window.Banner, insideEligibleResult.FunctionCallOutputText);
        }
        else
        {
            Assert.DoesNotContain(window.Banner, insideEligibleResult.FunctionCallOutputText);
        }

        // Inside this pack's own window: the non-eligible item is unaffected -- the persona-level
        // flag being active must not blanket-discount an item that never individually opted in.
        OrderScenarioHelpers.AssertMoneyEqual(
            expectation.IneligibleMenuPrice * (1 + taxRate),
            OrderScenarioHelpers.GetOrderFinalTotal(insideIneligibleResult.ToolResultJson!),
            $"persona '{personaId}': '{expectation.IneligibleItem}' has no happyHourDiscounted:true " +
            "opt-in -- this pack's own happy-hour flag being active must not blanket-discount it.");

        // One second before this pack's own window opens: no discount, no banner at all.
        OrderScenarioHelpers.AssertMoneyEqual(
            expectation.MenuPrice * (1 + taxRate),
            OrderScenarioHelpers.GetOrderFinalTotal(outsideEligibleResult.ToolResultJson!),
            $"persona '{personaId}': one second before its own happy-hour window opens, " +
            $"'{expectation.EligibleItem}' must be full price.");
        Assert.DoesNotContain(window.Banner, outsideEligibleResult.FunctionCallOutputText);
        Assert.DoesNotContain("HAPPY HOUR", outsideEligibleResult.FunctionCallOutputText);
    }

    /// <summary>
    /// Refs #127: "A pack with happy hour disabled (McDonald's once #112 lands) never discounts
    /// or announces at any pinned time." Reuses this pack's own existing
    /// PersonaSmokeExpectations orderable item (every discovered pack already has one, per
    /// PersonaSmokeCoverageTests) at two independently-chosen instants rather than requiring its
    /// own extra happy-hour smoke data it has no eligible item for.
    /// </summary>
    private static async Task RunDisabledPackProofAsync(string personaId, PersonaHappyHourConfig.PersonaPricing pricing, CancellationToken ct)
    {
        var expectation = PersonaHappyHourSmokeExpectations.For(personaId, isEnabled: false);
        Assert.Null(expectation);
        var smoke = PersonaSmokeExpectations.For(personaId);

        DateTimeOffset[] pinnedInstants =
        [
            DateTimeOffset.Parse("2026-07-04T14:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-01-15T02:00:00Z", CultureInfo.InvariantCulture),
        ];

        foreach (var instant in pinnedInstants)
        {
            ToolCallResult result = null!;
            await using var fixture = new RealPackHappyHourFixture(personaId, instant);
            await fixture.InitializeAsync();
            await fixture.RunAsync(async () =>
            {
                result = await RealPackHappyHourTestSupport.AddItemAndReadResultAsync(
                    fixture, personaId, smoke.OrderableItemName, smoke.OrderableItemSize, smoke.OrderableItemPrice, ct);
            });

            OrderScenarioHelpers.AssertMoneyEqual(
                smoke.OrderableItemPrice * (1 + pricing.TaxRate),
                OrderScenarioHelpers.GetOrderFinalTotal(result.ToolResultJson!),
                $"persona '{personaId}': pricing.happyHour is null -- '{smoke.OrderableItemName}' " +
                $"must be full price regardless of clock instant ({instant:O}).");
            Assert.DoesNotContain("HAPPY HOUR", result.FunctionCallOutputText);
        }
    }
}

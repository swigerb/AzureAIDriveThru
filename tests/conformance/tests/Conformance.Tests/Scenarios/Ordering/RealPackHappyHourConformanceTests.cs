using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Refs #127: a per-pack, brand-neutral happy-hour conformance proof over every REAL pack
/// discovered on disk (<see cref="ConformancePersonas.DiscoverFromDisk()"/>) -- every real pack
/// merged so far, and any future pack automatically the moment its own PR lands, with no change
/// needed here.
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
/// <c>ZoneInfo</c>), and two packs' windows need not overlap in UTC at all -- one real pack's
/// window is 14-16 in its own timezone, another real pack's is 14-17 in an entirely different
/// timezone, and a future pack could pick anything. So a single shared FixedClock instant across
/// every pack (as
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
/// (disabled -- a future decision-5 pack, once #112 lands) is proven never to discount or announce at ANY pinned
/// instant, reusing that pack's own smoke.json orderable item (<see
/// cref="PersonaSmokeExpectations.For"/>) rather than requiring its own extra happy-hour item data
/// it has no use for.
///
/// Mutation testing (this PR's own evidence, reverted before commit -- see the PR description):
/// temporarily hardcoding order_state.py's per-session happy-hour discount to a shared constant
/// (one real pack's own 0.5, ignoring each pack's own <c>priceMultiplier</c>) leaves that pack's
/// row green (0.5 coincidentally matches) but fails a different real pack's row (its own distinct
/// multiplier expected, 0.5 computed) --
/// demonstrating this Theory reads each pack's OWN multiplier rather than trusting a shared
/// default. Separately, hardcoding <c>_happy_hour_announce</c> to always suppress the banner
/// fails BOTH real packs' inside-window rows (each currently has <c>announce: true</c> and expects
/// its own banner text) -- demonstrating the banner assertion is genuinely exercised, not
/// vacuously true. A third mutation, exercised against a scratch personas directory rather than
/// against a real disabled pack (none exists on disk until #112 -- see the PR description for the
/// full setup and result): a disabled pack (<c>pricing.happyHour: null</c>) falling back to the
/// default persona's own window/announce/banner instead of staying inert fails the disabled-pack
/// row below at the instant derived from an enabled pack's own opening hour reinterpreted in the
/// disabled pack's own timezone -- demonstrating <see cref="RealPackHappyHourConformanceTests"/>'s
/// disabled-pack proof pinned
/// instants actually land inside the regression window rather than always missing it.
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

    /// <summary>
    /// Refs #127 nit (Rick's PR #133 review): same connection as <see
    /// cref="AddItemAndReadResultAsync"/>, but follows the add with a <c>get_order</c> round trip
    /// so the caller can assert the banner reaches get_order's own response text too, not just
    /// update_order's (which #115 already covers for the fixture packs).
    /// </summary>
    public static async Task<(ToolCallResult AddResult, ToolCallResult GetOrderResult)> AddItemAndGetOrderAsync(
        ConformanceFixture fixture, string persona, string itemName, string size, decimal price, CancellationToken ct)
    {
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(fixture, ct, persona: persona);
        await using var _ = browser;

        var added = await OrderScenarioHelpers.RunOrderStepsAsync(
            connection, browser,
            [("add", itemName, size, 1, price)],
            roundTripIndex, ct);

        var getOrder = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "get_order", "{}", "call_happy_hour_get_order", added.RoundTripIndex, ct);

        return (added, getOrder);
    }
}

public sealed class RealPackHappyHourConformanceTests
{
    /// <summary>
    /// Refs #127 item 1 (Rick's PR #133 review): one enabled pack's own window/timezone/banner,
    /// snapshotted so <see cref="RunDisabledPackProofAsync"/> can derive pinned instants and
    /// forbidden banner text from EVERY other enabled pack discovered on disk, never a literal.
    /// </summary>
    private sealed record EnabledPackSnapshot(string PersonaId, string TimeZoneId, PersonaHappyHourConfig.HappyHourWindow Window);

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
            var enabledPacks = ConformancePersonas.DiscoverFromDisk()
                .Where(id => id != personaId)
                .Select(id => (Id: id, Pricing: PersonaHappyHourConfig.Read(personasDir, id)))
                .Where(discovered => discovered.Pricing.HappyHour is not null)
                .Select(discovered => new EnabledPackSnapshot(discovered.Id, discovered.Pricing.TimeZoneId, discovered.Pricing.HappyHour!))
                .ToList();

            await RunDisabledPackProofAsync(personaId, pricing, enabledPacks, ct);
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

        // Refs #127 nit (Rick's PR #133 review): pins the CLOSING boundary the same way the
        // opening boundary already is above -- one second before this pack's own window closes
        // (still inside) vs the closing hour itself (first second outside) -- so a `<` vs `<=`
        // slip in order_state.py's own window-membership check fails here regardless of which
        // boundary it slips on.
        var insideClosingInstant = LocalWallClockInstant(timeZoneId, window.EndHour).AddSeconds(-1);
        var outsideClosingInstant = LocalWallClockInstant(timeZoneId, window.EndHour);

        // Refs #127 nit (Rick's PR #133 review): #115 already proves the banner echoes through
        // get_order for the fixture packs; this adds ONE get_order round trip for one real pack
        // -- the first discovered on disk, read from data so this file still carries no brand
        // literal of its own -- so a real pack's banner is proven to reach get_order's own
        // response text too, not just update_order's.
        var isGetOrderBannerCheckPack = personaId == ConformancePersonas.DiscoverFromDisk()[0];

        ToolCallResult insideEligibleResult = null!;
        ToolCallResult insideIneligibleResult = null!;
        ToolCallResult? insideEligibleGetOrderResult = null;
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
            if (isGetOrderBannerCheckPack)
            {
                await insideFixture.RunAsync(async () =>
                {
                    var (_, getOrderResult) = await RealPackHappyHourTestSupport.AddItemAndGetOrderAsync(
                        insideFixture, personaId, expectation.EligibleItem, expectation.Size, expectation.MenuPrice, ct);
                    insideEligibleGetOrderResult = getOrderResult;
                });
            }
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

        ToolCallResult insideClosingEligibleResult = null!;
        await using (var insideClosingFixture = new RealPackHappyHourFixture(personaId, insideClosingInstant))
        {
            await insideClosingFixture.InitializeAsync();
            await insideClosingFixture.RunAsync(async () =>
            {
                insideClosingEligibleResult = await RealPackHappyHourTestSupport.AddItemAndReadResultAsync(
                    insideClosingFixture, personaId, expectation.EligibleItem, expectation.Size, expectation.MenuPrice, ct);
            });
        }

        ToolCallResult outsideClosingEligibleResult = null!;
        await using (var outsideClosingFixture = new RealPackHappyHourFixture(personaId, outsideClosingInstant))
        {
            await outsideClosingFixture.InitializeAsync();
            await outsideClosingFixture.RunAsync(async () =>
            {
                outsideClosingEligibleResult = await RealPackHappyHourTestSupport.AddItemAndReadResultAsync(
                    outsideClosingFixture, personaId, expectation.EligibleItem, expectation.Size, expectation.MenuPrice, ct);
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

        // Refs #127 nit: the banner must reach get_order's own response text too, for the one
        // designated real pack, not just update_order's.
        if (isGetOrderBannerCheckPack && window.Announce)
        {
            Assert.Contains(window.Banner, insideEligibleGetOrderResult!.FunctionCallOutputText);
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

        // One second before this pack's own window closes: still discounted -- pins the closing
        // boundary the same way the opening boundary is pinned above.
        OrderScenarioHelpers.AssertMoneyEqual(
            expectation.MenuPrice * window.PriceMultiplier * (1 + taxRate),
            OrderScenarioHelpers.GetOrderFinalTotal(insideClosingEligibleResult.ToolResultJson!),
            $"persona '{personaId}': one second before its own happy-hour window closes, " +
            $"'{expectation.EligibleItem}' must still be discounted by this pack's own " +
            $"priceMultiplier ({window.PriceMultiplier}).");

        // Exactly at this pack's own window's closing hour: no longer discounted, no banner.
        OrderScenarioHelpers.AssertMoneyEqual(
            expectation.MenuPrice * (1 + taxRate),
            OrderScenarioHelpers.GetOrderFinalTotal(outsideClosingEligibleResult.ToolResultJson!),
            $"persona '{personaId}': at its own happy-hour window's closing hour, " +
            $"'{expectation.EligibleItem}' must be full price.");
        Assert.DoesNotContain(window.Banner, outsideClosingEligibleResult.FunctionCallOutputText);
        Assert.DoesNotContain("HAPPY HOUR", outsideClosingEligibleResult.FunctionCallOutputText);
    }

    /// <summary>
    /// Refs #127: "A pack with happy hour disabled (a future decision-5 pack once #112 lands)
    /// never discounts or announces at any pinned time." Reuses this pack's own existing
    /// PersonaSmokeExpectations orderable item (every discovered pack already has one, per
    /// PersonaSmokeCoverageTests) rather than requiring its own extra happy-hour smoke data it
    /// has no eligible item for.
    ///
    /// Refs #127 item 1 (Rick's PR #133 review): the two original fixed reference instants
    /// (09:00/10:00 and 20:00/21:00 local in the two real packs' timezones) sit outside every
    /// real pack's window, so a disabled pack silently falling back to the default persona's own
    /// window/announce/banner would pass both rows. <paramref name="enabledPacks"/> -- every OTHER
    /// pack discovered on disk with its OWN <c>pricing.happyHour</c> enabled -- fixes that: for
    /// each one, this method adds (a) the exact instant THAT pack's own window opens (in ITS OWN
    /// timezone), which is a real moment somewhere in the world a happy hour is genuinely active,
    /// and (b) that SAME start hour reinterpreted in THIS disabled pack's OWN timezone -- the one
    /// that actually bites the realistic fallback bug, because order_state.py always resolves
    /// happy-hour membership through a session's OWN bound timezone (see this pack's own
    /// <c>store.timezone</c>), so a fallback that borrows another pack's window/announce/banner
    /// but keeps this pack's own timezone shows up exactly there. At every instant, this pack must
    /// stay full price, silent on "HAPPY HOUR", AND silent on every enabled pack's own banner text
    /// -- read fresh from that pack's own persona.json, never a literal.
    /// </summary>
    private static async Task RunDisabledPackProofAsync(
        string personaId,
        PersonaHappyHourConfig.PersonaPricing pricing,
        IReadOnlyList<EnabledPackSnapshot> enabledPacks,
        CancellationToken ct)
    {
        var expectation = PersonaHappyHourSmokeExpectations.For(personaId, isEnabled: false);
        Assert.Null(expectation);
        var smoke = PersonaSmokeExpectations.For(personaId);

        var pinnedInstants = new List<DateTimeOffset>
        {
            DateTimeOffset.Parse("2026-07-04T14:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-01-15T02:00:00Z", CultureInfo.InvariantCulture),
        };
        foreach (var enabledPack in enabledPacks)
        {
            // (a) the real, global moment this enabled pack's own window opens.
            pinnedInstants.Add(LocalWallClockInstant(enabledPack.TimeZoneId, enabledPack.Window.StartHour));
            // (b) that same start hour, reinterpreted in THIS disabled pack's own timezone --
            // the instant a "falls back to the default persona's window" regression would bite,
            // since this pack's own timezone is always what happy-hour membership resolves
            // through, never the enabled pack's.
            pinnedInstants.Add(LocalWallClockInstant(pricing.TimeZoneId, enabledPack.Window.StartHour));
        }

        foreach (var instant in pinnedInstants.Distinct())
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

            // Refs #127 item 1: a disabled pack must never surface ANY other pack's own banner
            // text either -- not just the generic "HAPPY HOUR" substring above.
            foreach (var enabledPack in enabledPacks)
            {
                Assert.DoesNotContain(enabledPack.Window.Banner, result.FunctionCallOutputText);
            }
        }
    }
}

using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #76 part 2, Rick's wave-plan comment on #20: end-to-end proof that a session bound to
/// one persona's search tool never sees another persona's menu items, now that
/// <see cref="FakeSearchServer"/> is multi-index (routes by the route's own `indexName` instead
/// of always answering from one shared document set -- see its own doc comment). test-alpha and
/// test-beta are both ENABLED on <see cref="TwoPersonaConformanceFixture"/> and each has a
/// deliberately DISJOINT, brand-prefixed item catalog ("Alpha ..." vs "Beta ...", see their own
/// menu/menuItems.json under <see cref="RepoPaths.FixturePersonasDirectory"/>), so a session that
/// searched the WRONG persona's index (or a fake that ignored indexName entirely and always
/// answered from a single shared document set) would be caught immediately by the other
/// persona's item name(s) showing up where they never should.
///
/// A "*" query (FakeSearchServer.Filter's own wildcard branch, `_search_cfg.top_results` default
/// of 3 comfortably covers each pack's exactly-3-item catalog) returns every document loaded for
/// the session's OWN bound index -- the strongest form of this proof, since it doesn't depend on
/// term-matching happening to hit only one persona's catalog.
///
/// Deliberately UNTAGGED, same reasoning as <see cref="PersonaBusinessRuleConformanceTests"/> and
/// <see cref="PersonaDiscoveryConformanceTests"/>: the dotnet backend skeleton doesn't implement
/// the persona catalog yet.
/// </summary>
[Collection(TwoPersonaConformanceCollection.Name)]
public sealed class PersonaSearchIsolationConformanceTests(TwoPersonaConformanceFixture fixture)
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Test_alpha_session_search_returns_only_test_alpha_items_never_test_beta() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: TwoPersonaConformanceFixture.PersonaA);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search", """{"query":"*"}""",
            "call_isolation_alpha", roundTripIndex, ct, toClient: false);

        Assert.Contains("Alpha Cola", result.FunctionCallOutputText);
        Assert.Contains("Alpha Flavor Shot", result.FunctionCallOutputText);
        Assert.Contains("Alpha Burger", result.FunctionCallOutputText);
        Assert.DoesNotContain("Beta", result.FunctionCallOutputText);
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Test_beta_session_search_returns_only_test_beta_items_never_test_alpha() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: TwoPersonaConformanceFixture.PersonaB);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search", """{"query":"*"}""",
            "call_isolation_beta", roundTripIndex, ct, toClient: false);

        Assert.Contains("Beta Root Beer", result.FunctionCallOutputText);
        Assert.Contains("Beta Double Burger", result.FunctionCallOutputText);
        Assert.Contains("Beta Cheese Sauce", result.FunctionCallOutputText);
        Assert.DoesNotContain("Alpha", result.FunctionCallOutputText);
    });
}

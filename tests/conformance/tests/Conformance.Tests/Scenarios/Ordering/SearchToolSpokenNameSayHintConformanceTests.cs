using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// #313 ("item 7"/item 4a, coordinator fix-up round): proves the real, FakeSearchServer-backed
/// `search` tool call (SearchTool.cs, the C# port of tools.py's Azure-AI-Search-backed `search()`
/// -- not just a local in-process fallback) appends the "(say: ...)" pronunciation hint to a
/// matched item whose own catalog name has a `spokenName` override, exactly like
/// <see cref="SearchToolTests"/>'s happy-path row proves the plain `Item: ...` text. Runs against
/// <see cref="ZetaConformanceFixture"/>'s synthetic "ZORBS® Bite Treats" item (spokenName "Zorb
/// Bite Treats") so no real brand word is needed to exercise this.
/// </summary>
[Collection(ZetaConformanceCollection.Name)]
public sealed class SearchToolSpokenNameSayHintConformanceTests(ZetaConformanceFixture fixture)
{
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Search_appends_say_hint_for_an_item_with_a_spoken_name_override() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (browser, connection, roundTripIndex) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: ZetaConformanceFixture.PersonaId);
        await using var _ = browser;

        var result = await OrderScenarioHelpers.CallToolAsync(
            connection, browser, "search",
            """{"query":"zorbs bite treats"}""",
            "call_search_zorbs_say_hint", roundTripIndex, ct, toClient: false);

        Assert.False(string.IsNullOrWhiteSpace(result.FunctionCallOutputText));
        Assert.Contains("ZORBS® Bite Treats (say: Zorb Bite Treats)", result.FunctionCallOutputText);
    });
}

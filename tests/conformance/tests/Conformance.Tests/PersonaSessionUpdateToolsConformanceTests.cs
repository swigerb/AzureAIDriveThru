using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #170 round 3, R4 (Rick's round-2 review): every session's tool list (both the realtime
/// and the cascade backend) was built once from the DEPLOYMENT DEFAULT persona's own
/// <c>prompts/tool_schemas.yaml</c> (<c>tools.py</c>'s <c>attach_tools_rtmt</c> populated
/// <c>rtmt.tools</c> from one shared loader; C# already used the bound persona's own loader) -- so
/// a guest bound to a non-default persona heard that persona's own menu and prompt, but its tool
/// descriptions still named the DEFAULT persona's own brand and ticket/order-screen name. This is
/// the exact same coverage gap <see cref="PersonaSessionUpdateInstructionsConformanceTests"/>
/// closed for `session.instructions`, now closed for `session.tools[].description`: connect with
/// `?persona=&lt;id&gt;`, send the real client session.update, and assert the forwarded upstream
/// session's `search` tool description contains THAT pack's own text and none of the others'.
///
/// Same reasoning as <see cref="PersonaSessionUpdateInstructionsConformanceTests"/>: shared C#
/// here carries NO persona-specific literal of its own. Every persona's own
/// `toolDescriptionSubstring` lives in its own tests/conformance/testdata/personas/&lt;id&gt;/
/// smoke.json (same file both <see cref="PersonaSmokeExpectations"/> and <see
/// cref="PersonaSessionUpdateExpectations"/> already read), sourced from that SAME pack's own
/// prompts/tool_schemas.yaml `search` tool's own description -- the one tool every pack (real or
/// fixture) always defines itself, so the substring never comes from the hardcoded fallback that
/// `attach_tools_rtmt` uses for a tool name a pack's own yaml omits.
/// </summary>
internal static class PersonaSessionUpdateToolsExpectations
{
    public sealed record Expectation(string ToolDescriptionSubstring);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Reads one persona's own `toolDescriptionSubstring` from its existing smoke.json (<see
    /// cref="RepoPaths.PersonaSmokeDataPath"/>) -- the same per-persona data file <see
    /// cref="PersonaSmokeExpectations"/> and <see cref="PersonaSessionUpdateExpectations"/>
    /// already read, just one more field on it, so adding this pack's own R4 coverage never
    /// requires touching this shared C# file.
    /// </summary>
    public static Expectation For(string personaId)
    {
        var path = RepoPaths.PersonaSmokeDataPath(RepoPaths.FindRepoRoot(), personaId);
        Assert.True(File.Exists(path),
            $"PersonaSessionUpdateToolsConformanceTests.cs's PersonaSessionUpdateToolsExpectations.For('{personaId}') " +
            $"found no data file at '{path}'. Add a \"toolDescriptionSubstring\" field to it (a short, " +
            "em-dash-free, single-line prefix of this SAME persona's own prompts/tool_schemas.yaml " +
            "`search` tool's own \"description\" -- e.g. that description's first sentence, stopping " +
            "before any punctuation not safe to embed in a C# assertion).");

        var json = File.ReadAllText(path);
        var expectation = JsonSerializer.Deserialize<Expectation>(json, Options);
        Assert.True(expectation is not null, $"'{path}' deserialized to null.");
        Assert.False(string.IsNullOrWhiteSpace(expectation!.ToolDescriptionSubstring),
            $"'{path}' is missing a non-empty \"toolDescriptionSubstring\" field.");
        return expectation;
    }

    /// <summary>
    /// Issue #170 round 4, R6 (Rick's PR #175 round-3 review): the one shared assertion body
    /// used by BOTH <see cref="PersonaSessionUpdateToolsScenario"/> (the ordinary client-update
    /// path below) and <c>PersonaSessionUpdateFallbackScenario</c> in
    /// PersonaSessionUpdateFallbackConformanceTests.cs (the bootstrap and rejected-update
    /// fallback frames) -- those two call sites previously only asserted `instructions`, never
    /// `session.tools[].description`, so a mutation that swapped either frame's tool list for the
    /// deployment default's own (x-boot, x-recover, x-toclient on the Python backend;
    /// cs-boot-tools, cs-fallback-tools on the C# backend) survived every suite even though the
    /// ordinary client-update path's own equivalent mutation (R4) was already caught. Takes the
    /// already-parsed `session` object and a short, human-readable name for the frame being
    /// checked (e.g. "bootstrap session.update", "fallback session.update") purely for assertion
    /// messages -- no persona-specific literal lives here either, same discipline as <see
    /// cref="For"/> above.
    /// </summary>
    public static void AssertSearchToolDescriptionIsBoundTo(
        JsonElement session, string personaId, IReadOnlyList<string> allPersonaIdsOnThisFixture, string frameName)
    {
        var expected = For(personaId);

        Assert.True(session.TryGetProperty("tools", out var toolsProp),
            $"The {frameName} for persona '{personaId}' carried no 'tools' at all.");

        var searchTool = toolsProp.EnumerateArray()
            .FirstOrDefault(t => t.TryGetProperty("name", out var n) && n.GetString() == "search");
        Assert.True(searchTool.ValueKind != JsonValueKind.Undefined,
            $"The {frameName} for persona '{personaId}' carried no 'search' tool.");

        Assert.True(searchTool.TryGetProperty("description", out var descriptionProp),
            $"The 'search' tool in the {frameName} for persona '{personaId}' carried no 'description'.");
        var description = descriptionProp.GetString();
        Assert.True(!string.IsNullOrEmpty(description),
            $"The 'search' tool in the {frameName} for persona '{personaId}' carried empty 'description'.");

        // R4/R6's own regression: this pack's own tool description text must be present.
        Assert.Contains(expected.ToolDescriptionSubstring, description);

        // The live bug's exact symptom: no OTHER enabled persona's tool description text leaked
        // in -- i.e. the deployment default's (or any other bound persona's) own tool schemas
        // never silently substituted.
        foreach (var otherId in allPersonaIdsOnThisFixture)
        {
            if (otherId == personaId)
            {
                continue;
            }

            var other = For(otherId);
            Assert.DoesNotContain(other.ToolDescriptionSubstring, description);
        }
    }
}

/// <summary>Shared R4 scenario body -- run once per persona id by both Theory classes below.
/// <paramref name="allPersonaIdsOnThisFixture"/> is every persona id enabled on the SAME fixture
/// the Theory itself iterates (the full real-pack set for <see cref="ConformanceFixture"/>, or
/// exactly test-alpha/test-beta for <see cref="TwoPersonaConformanceFixture"/>) so the
/// "none of the others" half of the assertion never reaches across fixtures into packs that were
/// never enabled on this connection's own deployment.</summary>
file static class PersonaSessionUpdateToolsScenario
{
    public static async Task RunAsync(
        ConformanceFixture fixture, string personaId, IReadOnlyList<string> allPersonaIdsOnThisFixture, CancellationToken ct)
    {
        var (browser, connection, _) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId);
        await using var _browser = browser;

        // The browser-triggered session.update (Sequence > 0 -- Sequence 0 is the middle tier's
        // own bootstrap session.update, sent before any client traffic is relayed). Same idiom as
        // PersonaSessionUpdateInstructionsConformanceTests.cs's own scenario.
        var sessionUpdate = connection.ReceivedFrames.Snapshot().FirstOrDefault(f =>
            f.Sequence > 0 && f.Type == "session.update");
        Assert.True(sessionUpdate is not null,
            $"No browser-triggered session.update was forwarded upstream for persona '{personaId}'.");

        var session = sessionUpdate!.Json.GetProperty("session");
        PersonaSessionUpdateToolsExpectations.AssertSearchToolDescriptionIsBoundTo(
            session, personaId, allPersonaIdsOnThisFixture, "session.update forwarded upstream");
    }
}

/// <summary>Real packs under personas/ -- every pack <see cref="ConformancePersonas.DiscoverFromDisk()"/>
/// finds, on the fixture whose enabled set is that same full real-pack catalog.</summary>
[Collection(ConformanceCollection.Name)]
public sealed class RealPackPersonaSessionUpdateToolsConformanceTests(ConformanceFixture fixture)
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
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(DiscoveredPersonaIds))]
    public Task Client_session_update_carries_the_bound_personas_own_tool_descriptions(string personaId) => fixture.RunAsync(
        () => PersonaSessionUpdateToolsScenario.RunAsync(
            fixture, personaId, ConformancePersonas.DiscoverFromDisk(), TestContext.Current.CancellationToken));
}

/// <summary>Fixture packs under app/backend/tests/fixtures/personas/ -- test-alpha/test-beta, the
/// two personas <see cref="TwoPersonaConformanceFixture"/> actually enables.</summary>
[Collection(TwoPersonaConformanceCollection.Name)]
public sealed class FixturePackPersonaSessionUpdateToolsConformanceTests(TwoPersonaConformanceFixture fixture)
{
    private static readonly IReadOnlyList<string> FixturePersonaIds =
    [
        TwoPersonaConformanceFixture.PersonaA,
        TwoPersonaConformanceFixture.PersonaB,
    ];

    public static TheoryData<string> FixturePersonaIdsData()
    {
        var data = new TheoryData<string>
        {
            TwoPersonaConformanceFixture.PersonaA,
            TwoPersonaConformanceFixture.PersonaB,
        };
        return data;
    }

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(FixturePersonaIdsData))]
    public Task Client_session_update_carries_the_bound_personas_own_tool_descriptions(string personaId) => fixture.RunAsync(
        () => PersonaSessionUpdateToolsScenario.RunAsync(
            fixture, personaId, FixturePersonaIds, TestContext.Current.CancellationToken));
}

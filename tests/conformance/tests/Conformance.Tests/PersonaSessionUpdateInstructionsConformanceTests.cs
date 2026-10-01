using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #170 (live production bug): a guest bound to a non-default persona sent the client
/// `session.update` <c>useRealtime.tsx</c> sends on connect, and the middle tier rebuilt the
/// upstream session via <c>_build_session(...)</c> (Python) / <c>RealtimeSessionBuilder.
/// BuildSession(...)</c> (C#) WITHOUT this session's own bound persona's system prompt -- so it
/// silently fell back to the deployment default persona's instructions (live log evidence:
/// "Session created ... persona=&lt;non-default&gt;", then "Client session.update forwarded",
/// then the model greeting with the DEFAULT persona's identity instead).
///
/// <see cref="SmokeSessionBootstrapTests"/>'s
/// <c>Greeting_response_create_arrives_only_after_the_browser_session_update</c> already proves
/// the browser's session.update is forwarded (GA-translated) -- but it never asserts
/// `session.instructions`, which is exactly the coverage gap that let #170 ship. This file closes
/// that gap generically, for every discovered persona pack, on both legs: connect with
/// `?persona=&lt;id&gt;`, send the real client session.update, and assert the forwarded upstream
/// session's `instructions` contains THAT pack's own identity text and none of the others' --
/// plus (reusing <see cref="PersonaSmokeExpectations.GreetingSubstring"/>) that the greeting still
/// matches that same pack, so a persona mix-up can never slip through on the instructions check
/// alone while the greeting silently stays right (or vice versa).
///
/// Same reasoning as <see cref="PersonaSmokeExpectations"/>: shared C# here carries NO
/// persona-specific literal -- no pack id, no prompt text -- of its own. Every persona's own
/// `instructionsSubstring` lives in its own tests/conformance/testdata/personas/&lt;id&gt;/
/// smoke.json (same file <see cref="PersonaSmokeExpectations"/> already reads), sourced from that
/// SAME pack's own prompts/system_prompt.yaml IDENTITY section (priority 1, always assembled
/// first) -- never invented, never duplicating the prompt-assembly algorithm itself.
/// </summary>
internal static class PersonaSessionUpdateExpectations
{
    public sealed record Expectation(string InstructionsSubstring);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Reads one persona's own `instructionsSubstring` from its existing smoke.json (<see
    /// cref="RepoPaths.PersonaSmokeDataPath"/>) -- the same per-persona data file <see
    /// cref="PersonaSmokeExpectations"/> already reads, just one more field on it, so adding this
    /// pack's own #170 coverage never requires touching this shared C# file.
    /// </summary>
    public static Expectation For(string personaId)
    {
        var path = RepoPaths.PersonaSmokeDataPath(RepoPaths.FindRepoRoot(), personaId);
        Assert.True(File.Exists(path),
            $"PersonaSessionUpdateInstructionsConformanceTests.cs's PersonaSessionUpdateExpectations.For('{personaId}') " +
            $"found no data file at '{path}'. Add an \"instructionsSubstring\" field to it (a short, " +
            "em-dash-free, single-line prefix of this SAME persona's own prompts/system_prompt.yaml " +
            "IDENTITY section, priority 1 -- e.g. that section's first sentence, stopping before any " +
            "punctuation not safe to embed in a C# assertion).");

        var json = File.ReadAllText(path);
        var expectation = JsonSerializer.Deserialize<Expectation>(json, Options);
        Assert.True(expectation is not null, $"'{path}' deserialized to null.");
        Assert.False(string.IsNullOrWhiteSpace(expectation!.InstructionsSubstring),
            $"'{path}' is missing a non-empty \"instructionsSubstring\" field.");
        return expectation;
    }
}

/// <summary>Shared #170 scenario body -- run once per persona id by both Theory classes below.
/// <paramref name="allPersonaIdsOnThisFixture"/> is every persona id enabled on the SAME fixture
/// the Theory itself iterates (the full real-pack set for <see cref="ConformanceFixture"/>, or
/// exactly test-alpha/test-beta for <see cref="TwoPersonaConformanceFixture"/>) so the
/// "none of the others" half of the assertion never reaches across fixtures into packs that were
/// never enabled on this connection's own deployment.</summary>
file static class PersonaSessionUpdateInstructionsScenario
{
    public static async Task RunAsync(
        ConformanceFixture fixture, string personaId, IReadOnlyList<string> allPersonaIdsOnThisFixture, CancellationToken ct)
    {
        var expected = PersonaSessionUpdateExpectations.For(personaId);
        var greetingExpected = PersonaSmokeExpectations.For(personaId);

        var (browser, connection, _) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId);
        await using var _browser = browser;

        // The browser-triggered session.update (Sequence > 0 -- Sequence 0 is the middle tier's
        // own bootstrap session.update, sent before any client traffic is relayed). Same idiom as
        // SmokeSessionBootstrapTests.cs's Greeting_response_create_arrives_only_after_the_browser_
        // session_update, which proves this frame is forwarded but never inspects `instructions`.
        var sessionUpdate = connection.ReceivedFrames.Snapshot().FirstOrDefault(f =>
            f.Sequence > 0 && f.Type == "session.update");
        Assert.True(sessionUpdate is not null,
            $"No browser-triggered session.update was forwarded upstream for persona '{personaId}'.");

        var session = sessionUpdate!.Json.GetProperty("session");
        Assert.True(session.TryGetProperty("instructions", out var instructionsProp),
            $"The session.update forwarded upstream for persona '{personaId}' carried no " +
            "'instructions' at all -- #170 regressed (the bound persona's system prompt is " +
            "no longer threaded through the client-update rebuild).");
        var instructions = instructionsProp.GetString();
        Assert.True(!string.IsNullOrEmpty(instructions),
            $"The session.update forwarded upstream for persona '{personaId}' carried empty " +
            "'instructions'.");

        // #170's own regression: this pack's own identity text must be present.
        Assert.Contains(expected.InstructionsSubstring, instructions);

        // The live bug's exact symptom: no OTHER enabled persona's identity text leaked in --
        // i.e. the deployment default (or any other bound persona) never silently substituted.
        foreach (var otherId in allPersonaIdsOnThisFixture)
        {
            if (otherId == personaId)
            {
                continue;
            }

            var other = PersonaSessionUpdateExpectations.For(otherId);
            Assert.DoesNotContain(other.InstructionsSubstring, instructions);
        }

        // The greeting round trip (already awaited by ConnectAndGreetAsync) must match the SAME
        // pack too -- proves the instructions check above and the greeting can't silently
        // disagree about which persona this session is actually bound to.
        var greetingFrame = connection.ReceivedFrames.Snapshot().FirstOrDefault(f =>
            f.Type == "conversation.item.create" &&
            f.Json.TryGetProperty("item", out var item) &&
            item.TryGetProperty("type", out var itemType) &&
            itemType.GetString() == "message");
        Assert.True(greetingFrame is not null,
            $"No greeting conversation.item.create (item.type=message) frame found for persona '{personaId}'.");
        var greetingText = greetingFrame!.Json.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains(greetingExpected.GreetingSubstring, greetingText);
    }
}

/// <summary>Real packs under personas/ -- every pack <see cref="ConformancePersonas.DiscoverFromDisk()"/>
/// finds, on the fixture whose enabled set is that same full real-pack catalog.</summary>
[Collection(ConformanceCollection.Name)]
public sealed class RealPackPersonaSessionUpdateConformanceTests(ConformanceFixture fixture)
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
    public Task Client_session_update_carries_the_bound_personas_own_instructions(string personaId) => fixture.RunAsync(
        () => PersonaSessionUpdateInstructionsScenario.RunAsync(
            fixture, personaId, ConformancePersonas.DiscoverFromDisk(), TestContext.Current.CancellationToken));
}

/// <summary>Fixture packs under app/backend/tests/fixtures/personas/ -- test-alpha/test-beta, the
/// two personas <see cref="TwoPersonaConformanceFixture"/> actually enables.</summary>
[Collection(TwoPersonaConformanceCollection.Name)]
public sealed class FixturePackPersonaSessionUpdateConformanceTests(TwoPersonaConformanceFixture fixture)
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
    public Task Client_session_update_carries_the_bound_personas_own_instructions(string personaId) => fixture.RunAsync(
        () => PersonaSessionUpdateInstructionsScenario.RunAsync(
            fixture, personaId, FixturePersonaIds, TestContext.Current.CancellationToken));
}

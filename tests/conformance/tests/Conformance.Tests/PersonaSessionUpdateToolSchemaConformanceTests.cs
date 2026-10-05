using System.Linq;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #315 (would have caught #314): the fake upstream now validates every
/// `session.tools[*].parameters` against the JSON Schema metaschema the same way the real
/// realtime API does (<see cref="GaSessionValidator"/>'s new tool-schema check). This file closes
/// the loop the issue asked for -- boot EVERY shipped persona pack (<see
/// cref="ConformancePersonas.DiscoverFromDisk"/>, the real packs under personas/, not the
/// test-only fixture packs) and assert that pack's own bootstrap AND browser-triggered
/// `session.update` are both ACCEPTED by the stricter fake, i.e. this repo never ships a
/// `tool_schemas.yaml` that the real API would itself reject.
///
/// Calls <see cref="GaSessionValidator.Validate"/> directly against the exact frames the fake
/// server captured on the wire, rather than inferring acceptance from side effects (the greeting
/// completing, or the absence of a logged rejection) -- the backend's own rejection-recovery path
/// can make a REJECTED session.update's symptom invisible to the greeting (it resends a minimal
/// fallback and the conversation carries on), so asserting directly on the validator's own verdict
/// is the only way this test can't be fooled by that recovery path. The <see
/// cref="OrderScenarioHelpers.ConnectAndGreetAsync"/>-level `CapturedProcessOutput` guard (added
/// for the same issue) is the complementary, broader net: it catches a rejection landing in ANY
/// of the standard ordering scenarios, not just this one, but can only assert "something was
/// rejected", not which pack's schema was at fault -- this file's per-persona Theory gives that
/// detail back.
///
/// Mutation check (per the issue): with #314 reverted, `PromptLoader` hands every C# session a
/// `tool_schemas.yaml`-sourced `additionalProperties` typed as the STRING `"false"` rather than
/// the JSON boolean `false` -- this test's own `ValidateSchemaNode` rejects that shape, so the C#
/// leg goes red exactly as the issue requires. Confirmed by hand (not re-run automatically on
/// every CI pass): reverting b16b0b8 and running this Theory fails every discovered persona id on
/// the C# leg with `invalid_function_parameters`.
/// </summary>
file static class PersonaSessionUpdateToolSchemaScenario
{
    public static async Task RunAsync(ConformanceFixture fixture, string personaId, CancellationToken ct)
    {
        var (browser, connection, _) = await OrderScenarioHelpers.ConnectAndGreetAsync(
            fixture, ct, persona: personaId);
        await using var _browser = browser;

        var frames = connection.ReceivedFrames.Snapshot();

        var bootstrap = frames.FirstOrDefault(f => f.Sequence == 0 && f.Type == "session.update");
        Assert.True(bootstrap is not null,
            $"No bootstrap session.update was captured for persona '{personaId}'.");

        var clientUpdate = frames.FirstOrDefault(f => f.Sequence > 0 && f.Type == "session.update");
        Assert.True(clientUpdate is not null,
            $"No browser-triggered session.update was forwarded upstream for persona '{personaId}'.");

        foreach (var (frame, frameName) in new (RecordedFrame Frame, string Name)[]
                 {
                     (bootstrap!, "bootstrap"),
                     (clientUpdate!, "client"),
                 })
        {
            var result = GaSessionValidator.Validate(
                frame.Json, new RealtimeSessionState(), BackendContract.DefaultDeployment);
            Assert.True(result.IsAccepted,
                $"The {frameName} session.update for persona '{personaId}' was REJECTED by the " +
                $"stricter fake (code={result.Code}, param={result.Param}, message=" +
                $"{result.Message}) -- this shipped persona pack's own tool schema would be " +
                "rejected by the real realtime API too (issue #315).");
        }
    }
}

/// <summary>Real packs under personas/ -- every pack <see cref="ConformancePersonas.DiscoverFromDisk()"/>
/// finds, i.e. every pack this repo actually SHIPS, on the same shared fixture/collection every
/// other real-pack persona Theory in this suite already uses.</summary>
[Collection(ConformanceCollection.Name)]
public sealed class PersonaSessionUpdateToolSchemaConformanceTests(ConformanceFixture fixture)
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
    public Task Shipped_personas_bootstrap_and_client_session_update_are_accepted_by_the_stricter_fake(string personaId) =>
        fixture.RunAsync(() => PersonaSessionUpdateToolSchemaScenario.RunAsync(
            fixture, personaId, TestContext.Current.CancellationToken));
}

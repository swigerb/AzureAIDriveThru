using System.Linq;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #170 round 2 (Rick's PR #175 review, item R1): <see
/// cref="PersonaSessionUpdateInstructionsConformanceTests"/> already proves the bound persona's
/// own instructions survive the ORDINARY client session.update rebuild, but it never exercises
/// the REJECTED-update fallback path (<c>_recover_rejected_session_update</c> / C#'s
/// <c>RealtimeProcessor.HandleErrorAsync</c> --&gt; <c>RealtimeSessionBuilder.BuildFallbackSessionUpdate</c>).
/// Both of Rick's round-1 mutations on this path survived every existing test: (b) the Python
/// fallback silently dropping the bound `system_message` kwarg and falling back to
/// `self.system_message` (the deployment default), and the C# equivalent forcing the default
/// persona's prompt into the fallback's `instructions` instead of this session's own bound
/// <c>promptLoader.SystemPrompt</c> -- because every existing fallback scenario
/// (<see cref="Conformance.Tests.SessionUpdateFallbackTests"/>) only ever ran on the single
/// default-persona connection, so a bug that replaces ANY persona's instructions with the
/// default's would still pass (the default's own fallback already matches the default's own
/// instructions trivially).
///
/// This file closes that gap generically, for every discovered persona pack, on both legs: arm
/// <see cref="FakeRealtimeUpstreamServer.RejectNextSessionUpdates"/> for exactly the bootstrap
/// session.update, connect with `?persona=&lt;id&gt;`, and assert the FALLBACK session.update's
/// `instructions` contains THAT pack's own identity text and none of the others' -- reusing the
/// very same per-persona `instructionsSubstring` data <see
/// cref="PersonaSessionUpdateInstructionsConformanceTests"/>'s <c>PersonaSessionUpdateExpectations</c>
/// already reads from each pack's own smoke.json, so this file carries no persona-specific
/// literal of its own either.
///
/// Issue #170 round 4, R6 (Rick's PR #175 round-3 review): the bootstrap (Sequence 0, captured
/// below as <c>bootstrap</c>) and the fallback itself were already being captured by this
/// scenario for the `instructions` assertion above, but their own `session.tools[].description`
/// went unchecked -- so a mutation swapping either frame's tool list for the deployment
/// default's own (x-boot, x-recover on Python; cs-boot-tools, cs-fallback-tools on C#) survived
/// every suite. Both frames now also get <see
/// cref="PersonaSessionUpdateToolsExpectations.AssertSearchToolDescriptionIsBoundTo"/> -- the
/// SAME shared helper <see cref="PersonaSessionUpdateToolsScenario"/> in
/// PersonaSessionUpdateToolsConformanceTests.cs uses for the ordinary client-update path, so this
/// file still carries no persona-specific literal of its own for the tools half either.
/// </summary>
file static class PersonaSessionUpdateFallbackScenario
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    public static async Task RunAsync(
        ConformanceFixture fixture, string personaId, IReadOnlyList<string> allPersonaIdsOnThisFixture, CancellationToken ct)
    {
        var expected = PersonaSessionUpdateExpectations.For(personaId);
        var greetingExpected = PersonaSmokeExpectations.For(personaId);

        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open -- a previous test leaked a connection.");

        // Reject exactly the bootstrap (the first session.update this connection sends) so the
        // backend's own rejection-recovery path rebuilds and resends it as a fallback -- echoing
        // the rejected frame's own event_id exercises the guard's event_id-keyed correlation path
        // (see SessionUpdateFallbackTests.cs's own doc comment for the two correlation paths).
        fixture.Realtime.RejectNextSessionUpdates(1, code: "invalid_value", echoEventId: true);

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, persona: personaId, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null,
            $"No upstream connection was accepted within {FrameTimeout} for persona '{personaId}'.");

        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, $"Bootstrap session.update never arrived for persona '{personaId}'.");

        // At this point the browser hasn't sent its own session.update yet (that only happens
        // below), so the first session.update after the bootstrap can only be the fallback --
        // no race with the browser's own update is possible here, matching
        // SessionUpdateFallbackTests.cs's own ordering argument.
        var fallback = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > bootstrap!.Sequence && f.Type == "session.update", FrameTimeout, ct);
        Assert.True(fallback is not null,
            $"Expected a fallback session.update after the bootstrap's rejection for persona '{personaId}'.");

        var fallbackSession = fallback!.Json.GetProperty("session");
        Assert.True(fallbackSession.TryGetProperty("instructions", out var instructionsProp),
            $"The fallback session.update for persona '{personaId}' carried no 'instructions' at " +
            "all -- the bound persona's system prompt is no longer threaded through the " +
            "rejected-update recovery path.");
        var instructions = instructionsProp.GetString();
        Assert.True(!string.IsNullOrEmpty(instructions),
            $"The fallback session.update for persona '{personaId}' carried empty 'instructions'.");

        // #170 R1's own regression: this pack's own identity text must be present in the
        // FALLBACK, not just the (here, rejected) bootstrap.
        Assert.Contains(expected.InstructionsSubstring, instructions);

        // The exact symptom Rick's round-1 review flagged: no OTHER enabled persona's identity
        // text (in particular the deployment default's) silently substituted into the fallback.
        foreach (var otherId in allPersonaIdsOnThisFixture)
        {
            if (otherId == personaId)
            {
                continue;
            }

            var other = PersonaSessionUpdateExpectations.For(otherId);
            Assert.DoesNotContain(other.InstructionsSubstring, instructions);
        }

        // #170 round 4, R6: the bootstrap's own tool list (Sequence 0, rejected above) and the
        // fallback's own tool list must EACH carry this pack's own `search` tool description --
        // the bootstrap is built fresh from this session's own bound tool_schemas just like the
        // fallback is, and both are exactly the two call sites Rick's round-2 spec named
        // explicitly but that were never actually checked here before.
        var bootstrapSession = bootstrap!.Json.GetProperty("session");
        PersonaSessionUpdateToolsExpectations.AssertSearchToolDescriptionIsBoundTo(
            bootstrapSession, personaId, allPersonaIdsOnThisFixture, "bootstrap session.update");
        PersonaSessionUpdateToolsExpectations.AssertSearchToolDescriptionIsBoundTo(
            fallbackSession, personaId, allPersonaIdsOnThisFixture, "fallback session.update");

        // The greeting must still complete and match the SAME pack -- proves the fallback's own
        // recovery doesn't leave the session half-configured or silently bound to a different
        // persona than the one that greets.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var roundTripToken = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTripToken is not null,
            $"extension.round_trip_token never reached the browser for persona '{personaId}' " +
            "(greeting never completed after the fallback recovery).");

        var greetingFrame = connection.ReceivedFrames.Snapshot().FirstOrDefault(f =>
            f.Type == "conversation.item.create" &&
            f.Json.TryGetProperty("item", out var item) &&
            item.TryGetProperty("type", out var itemType) &&
            itemType.GetString() == "message");
        Assert.True(greetingFrame is not null, $"No greeting conversation.item.create (item.type=message) frame found for persona '{personaId}'.");
        var greetingText = greetingFrame!.Json.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains(greetingExpected.GreetingSubstring, greetingText);
    }
}

/// <summary>
/// Same real-pack discovery/deployment/profile as <see cref="ConformanceFixture"/> itself (every
/// property left at its default) -- the only reason this subclass exists at all is to get its OWN
/// dedicated collection/backend process. Rejecting a bootstrap's `reasoning` (this fixture's
/// deployment is reasoning-capable by name, same as the plain <see cref="ConformanceFixture"/>)
/// flips rtmt.py's `_reasoning_rejected` latch process-wide for the rest of that backend's
/// lifetime (see <see cref="SessionUpdateFallbackTests"/>'s own doc comment). Running this file's
/// scenario on the SHARED <see cref="ConformanceFixture"/>/<see cref="ConformanceCollection"/>
/// would make <see cref="Conformance.Tests.ReasoningSentForDefaultDeploymentTests"/> (which
/// asserts `reasoning` IS present on that same fixture's bootstrap) order-dependent on whether one
/// of this file's Theory rows happened to run first -- confirmed by observation: running this
/// scenario on the shared fixture made that unrelated test fail intermittently depending on xUnit's
/// (unordered) Theory row scheduling. <see cref="SessionUpdateFallbackTests"/> already established
/// this exact isolation discipline (its own dedicated <see cref="Gpt15ForcedReasoningConformanceFixture"/>)
/// for the same reason -- this fixture applies that same discipline here.
/// </summary>
public sealed class RealPackFallbackConformanceFixture : ConformanceFixture;

[CollectionDefinition(Name)]
public sealed class RealPackFallbackConformanceCollection : ICollectionFixture<RealPackFallbackConformanceFixture>
{
    public const string Name = "ConformanceRealPackFallback";
}

/// <summary>Real packs under personas/ -- every pack <see cref="ConformancePersonas.DiscoverFromDisk()"/>
/// finds, on a fixture whose enabled set is that same full real-pack catalog but whose process is
/// isolated from <see cref="ConformanceFixture"/>'s own shared one (see
/// <see cref="RealPackFallbackConformanceFixture"/>'s own doc comment).</summary>
[Collection(RealPackFallbackConformanceCollection.Name)]
public sealed class RealPackPersonaSessionUpdateFallbackConformanceTests(RealPackFallbackConformanceFixture fixture)
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
    public Task Rejected_bootstrap_recovers_via_a_fallback_carrying_the_bound_personas_own_instructions(string personaId) => fixture.RunAsync(
        () => PersonaSessionUpdateFallbackScenario.RunAsync(
            fixture, personaId, ConformancePersonas.DiscoverFromDisk(), TestContext.Current.CancellationToken),
        // Up to two deterministic ERROR-level log lines are this scenario's own subject matter:
        // "Upstream REJECTED session.update ..." (the rejection itself) and, the first time any
        // test flips it on this fixture's own dedicated process, "Deployment ... rejected
        // reasoning-model options ..." -- this fixture's bootstrap legitimately carries
        // `reasoning` (a reasoning-capable deployment, matching SessionUpdateFallbackTests.cs's
        // own allowedNewBackendErrors: 2 on the same deployment), and _reasoning_rejected is a
        // process-wide latch that only logs its own flip once per process, so whichever
        // persona's Theory row happens to run first (xUnit does not order Theory data) is the one
        // that may observe both lines; every later row only sees the first. Both are proven
        // recovered above (exactly one fallback, the greeting still completes), not an
        // unexpected/unhandled error.
        allowedNewBackendErrors: 2);
}

/// <summary>Fixture packs under app/backend/tests/fixtures/personas/ -- test-alpha/test-beta, the
/// two personas <see cref="TwoPersonaConformanceFixture"/> actually enables.</summary>
[Collection(TwoPersonaConformanceCollection.Name)]
public sealed class FixturePackPersonaSessionUpdateFallbackConformanceTests(TwoPersonaConformanceFixture fixture)
{
    public static TheoryData<string> FixturePersonaIdsData()
    {
        var data = new TheoryData<string>
        {
            TwoPersonaConformanceFixture.PersonaA,
            TwoPersonaConformanceFixture.PersonaB,
        };
        return data;
    }

    private static readonly IReadOnlyList<string> FixturePersonaIds =
    [
        TwoPersonaConformanceFixture.PersonaA,
        TwoPersonaConformanceFixture.PersonaB,
    ];

    [Theory]
    [Trait("Dotnet", "ready")]
    [MemberData(nameof(FixturePersonaIdsData))]
    public Task Rejected_bootstrap_recovers_via_a_fallback_carrying_the_bound_personas_own_instructions(string personaId) => fixture.RunAsync(
        () => PersonaSessionUpdateFallbackScenario.RunAsync(
            fixture, personaId, FixturePersonaIds, TestContext.Current.CancellationToken),
        // TwoPersonaConformanceFixture is its own dedicated collection/process already (see its
        // own doc comment), separate from ConformanceFixture's shared one -- no isolation concern
        // here, but the same reasoning-capable deployment means the same up-to-two-line (rejection
        // + one-time process-wide reasoning-latch flip) scenario as the real-pack class above.
        allowedNewBackendErrors: 2);
}

using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8, updated for Rick's PR #106 review item 1 (issue #75): `reasoning` is now sent in the
/// bootstrap session.update purely per the model catalog's own static `reasoning` flag for
/// whichever model the session is actually bound to (`config.yaml`'s `models.catalog`;
/// `processors.py::ResolvedModel.reasoning`) — see `rtmt.py`'s `_reasoning_model`, whose
/// `reasoning_override` (sourced from the bound model's catalog entry) now wins BEFORE the
/// deployment-name heuristic (`_NON_REASONING_DEPLOYMENT_RE`) or the explicit
/// `AZURE_OPENAI_REALTIME_REASONING_MODEL` switch even get consulted, for every session with a
/// bound model (i.e. every live session; there is no more default-path special case). Neither the
/// deployment name NOR the explicit switch can override the catalog any more — both are now only
/// reachable, defensively, before any session/model exists at all. Every fixture below still
/// binds the same persona default (sonic's own, `gpt-realtime-2.1`, catalog `reasoning: true`) —
/// see <see cref="ModelSelectionConformanceTests.Reasoning_is_sent_only_for_a_catalog_reasoning_model_not_the_other_selectable_one"/>
/// for the row that actually varies the CATALOG's own reasoning flag by binding a different model
/// (`gpt-realtime-mini`, `reasoning: false`) — deployment name and the reasoning switch are
/// deliberately varied here instead, to prove neither one governs the outcome any more, only the
/// catalog does. Each deployment name/switch value still gets its own dedicated fixture/collection
/// (its own Python process) — see Scenarios/Sessions/ReasoningDeploymentFixtures.cs.
/// </summary>
public static class ReasoningByDeploymentTestHelpers
{
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    public static async Task<System.Text.Json.JsonElement> ConnectAndGetBootstrapSessionAsync(
        ConformanceFixture fixture, CancellationToken ct)
    {
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var bootstrap = await connection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(bootstrap is not null, "Bootstrap session.update never arrived.");
        Assert.Equal("session.update", bootstrap!.Type);
        return bootstrap.Json.GetProperty("session");
    }
}

[Collection(ConformanceCollection.Name)]
public sealed class ReasoningSentForDefaultDeploymentTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Reasoning_effort_is_sent_in_the_bootstrap_for_the_default_reasoning_capable_deployment() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await ReasoningByDeploymentTestHelpers.ConnectAndGetBootstrapSessionAsync(fixture, ct);

        Assert.True(session.TryGetProperty("reasoning", out var reasoning),
            $"Expected `reasoning` on the bootstrap session.update for deployment " +
            $"'{BackendContract.DefaultDeployment}' (reasoning-capable by name).");
        Assert.Equal("low", reasoning.GetProperty("effort").GetString());
    });
}

[Collection(Gpt21DzConformanceCollection.Name)]
public sealed class ReasoningSentForDzDeploymentTests(Gpt21DzConformanceFixture fixture)
{
    [Fact]
    public Task Reasoning_effort_is_sent_in_the_bootstrap_for_a_gpt_realtime_2_1_dz_deployment() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await ReasoningByDeploymentTestHelpers.ConnectAndGetBootstrapSessionAsync(fixture, ct);

        Assert.True(session.TryGetProperty("reasoning", out var reasoning),
            "Expected `reasoning` on the bootstrap session.update for a gpt-realtime-2.1-dz deployment.");
        Assert.Equal("low", reasoning.GetProperty("effort").GetString());
    });
}

// RETIRED (Rick's PR #106 review item 1, issue #75): this class used to prove `reasoning` is
// NEVER sent for a gpt-realtime-1.5-named deployment purely by name. That's no longer true --
// `reasoning` is now governed entirely by the bound model's own catalog `reasoning` flag (see
// this file's own top-level doc comment), independent of deployment name. Re-proving "sent
// despite a 1.5-style name" here specifically would need this same
// Gpt15ConformanceFixture/Gpt15ConformanceCollection shared backend process -- but the fake
// upstream's GaSessionValidator still genuinely rejects `reasoning` for any "1.5"-named
// deployment (real GA behaviour, unrelated to this catalog change), which would flip rtmt.py's
// process-wide `_reasoning_rejected` latch (`_reasoning_model`: "a runtime rejection always
// wins" -- even over the catalog override) for the REST of this shared collection's lifetime,
// silently breaking SecondSessionUpdateRejectionLoopGuardTests
// (Scenarios/Sessions/SessionUpdateFallbackTests.cs), which deliberately relies on this fixture
// never tripping that latch on its own. The catalog-governs-independent-of-deployment-name
// property is instead proven on a fixture with no "1.5"-style deployment name at all --
// ModelSelectionConformanceTests.Reasoning_is_sent_only_for_a_catalog_reasoning_model_not_the_other_selectable_one
// (Scenarios/Sessions/ModelSelectionConformanceTests.cs), which varies the bound MODEL (not the
// deployment name) to get a genuine `reasoning: false` catalog entry, with neither connection
// ever rejected.

/// <summary>
/// PR #42 review item 10, superseded by Rick's PR #106 review item 1 (issue #75): this used to
/// prove AZURE_OPENAI_REALTIME_REASONING_MODEL=false beats a reasoning-capable deployment NAME.
/// It still does (the deployment-name heuristic is unreachable once any session is bound to a
/// model) -- but now the model catalog's own `reasoning` flag for the session's actually-bound
/// model beats the explicit switch too (`rtmt.py::_reasoning_model`: catalog `reasoning_override`
/// is checked before `self.reasoning_model`, the switch). This fixture's session still binds
/// sonic's own realtime default, gpt-realtime-2.1 (catalog `reasoning: true`), so `reasoning` is
/// now expected to be SENT despite the switch being explicitly false, proving the catalog -- not
/// the switch, and not the deployment name -- governs the outcome.
/// </summary>
[Collection(Gpt21ReasoningSwitchOffConformanceCollection.Name)]
public sealed class ReasoningSwitchOffIsOverriddenByTheBoundModelsCatalogEntryTests(Gpt21ReasoningSwitchOffConformanceFixture fixture)
{
    [Fact]
    public Task Reasoning_is_sent_for_the_catalog_reasoning_default_even_when_the_switch_is_explicitly_false() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await ReasoningByDeploymentTestHelpers.ConnectAndGetBootstrapSessionAsync(fixture, ct);

        Assert.True(session.TryGetProperty("reasoning", out var reasoning),
            $"AZURE_OPENAI_REALTIME_REASONING_MODEL=false no longer wins: the session is bound to " +
            "sonic's realtime default (gpt-realtime-2.1, catalog reasoning: true), and the catalog " +
            "now governs `reasoning` regardless of the explicit switch (Rick's PR #106 review item 1).");
        Assert.Equal("low", reasoning.GetProperty("effort").GetString());
    });
}

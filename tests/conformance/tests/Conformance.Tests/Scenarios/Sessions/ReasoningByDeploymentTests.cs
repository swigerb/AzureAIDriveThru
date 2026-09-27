using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8, updated for PR #106 review round 3 (Rick, issue #75): `_reasoning_model`'s precedence is a
/// runtime rejection latch first, then the explicit `AZURE_OPENAI_REALTIME_REASONING_MODEL`
/// switch (true/false -- an operator-level kill switch), then the model catalog's own static
/// `reasoning` flag for whichever model the session is actually bound to (`config.yaml`'s
/// `models.catalog`; `processors.py::ResolvedModel.reasoning`), and only then the deployment-name
/// heuristic (`_NON_REASONING_DEPLOYMENT_RE`) -- reachable only when the switch is `auto` and no
/// model is bound yet. Round 2 had briefly let the catalog win over an explicit switch; that's
/// wrong, since the catalog records what a model supports while the switch records what THIS
/// environment allows, and an explicit operator choice must be able to override what the catalog
/// claims. Every fixture below still binds the same persona default (sonic's own,
/// `gpt-realtime-2.1`, catalog `reasoning: true`) — see
/// <see cref="ModelSelectionConformanceTests.Reasoning_is_sent_only_for_a_catalog_reasoning_model_not_the_other_selectable_one"/>
/// for the row that actually varies the CATALOG's own reasoning flag by binding a different model
/// (`gpt-realtime-mini`, `reasoning: false`), with the switch left on `auto`. Each deployment
/// name/switch value still gets its own dedicated fixture/collection (its own Python process) —
/// see Scenarios/Sessions/ReasoningDeploymentFixtures.cs.
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

[Collection(Gpt15ConformanceCollection.Name)]
public sealed class ReasoningSwitchFalseKeepsReasoningOffA15DeploymentTests(Gpt15ConformanceFixture fixture)
{
    [Fact]
    public Task Reasoning_is_never_sent_in_the_bootstrap_for_a_gpt_realtime_1_5_deployment() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await ReasoningByDeploymentTestHelpers.ConnectAndGetBootstrapSessionAsync(fixture, ct);

        Assert.False(session.TryGetProperty("reasoning", out _),
            "This fixture's session is bound to sonic's realtime default (gpt-realtime-2.1, " +
            "catalog reasoning: true), but AZURE_OPENAI_REALTIME_REASONING_MODEL=false -- how an " +
            "operator actually runs a 1.5 deployment -- must still win over the catalog and keep " +
            "`reasoning` off entirely (PR #106 review round 3, Rick): gpt-realtime-1.5 rejects " +
            "`reasoning` and drops the whole session.update, tools included.");
    });
}

/// <summary>
/// PR #42 review item 10, revisited for PR #106 review round 3 (Rick, issue #75): this used to
/// prove AZURE_OPENAI_REALTIME_REASONING_MODEL=false beats a reasoning-capable deployment NAME,
/// then briefly (round 2) was inverted to prove the catalog beat the switch. Round 3 restores the
/// original relationship: the explicit switch is an operator-level kill switch that outranks BOTH
/// the deployment name AND the catalog's own `reasoning` flag for the bound model. This fixture's
/// session still binds sonic's own realtime default, gpt-realtime-2.1 (catalog `reasoning: true`),
/// so `reasoning` must be withheld despite the catalog saying the bound model supports it.
/// </summary>
[Collection(Gpt21ReasoningSwitchOffConformanceCollection.Name)]
public sealed class ReasoningSwitchOffOverridesTheBoundModelsCatalogEntryTests(Gpt21ReasoningSwitchOffConformanceFixture fixture)
{
    [Fact]
    public Task Reasoning_is_not_sent_when_the_switch_is_explicitly_false_even_for_a_catalog_reasoning_model() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await ReasoningByDeploymentTestHelpers.ConnectAndGetBootstrapSessionAsync(fixture, ct);

        Assert.False(session.TryGetProperty("reasoning", out _),
            "The session is bound to sonic's realtime default (gpt-realtime-2.1, catalog " +
            "reasoning: true), but AZURE_OPENAI_REALTIME_REASONING_MODEL=false must still win " +
            "over the catalog (PR #106 review round 3, Rick): the switch records what this " +
            "environment allows, and an explicit operator choice overrides what the catalog " +
            "claims a model supports.");
    });
}

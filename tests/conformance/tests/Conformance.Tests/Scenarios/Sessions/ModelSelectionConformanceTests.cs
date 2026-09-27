using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #106 review item 2: conformance rows for issue #75's per-session realtime model
/// selection (design doc sections 5.2/7.3/7.5). Deliberately UNTAGGED (no
/// <c>[Trait("Dotnet", "ready")]</c>), same reasoning as <see cref="PersonaDiscoveryConformanceTests"/>
/// and <see cref="PersonaMismatchConformanceTests"/> -- the dotnet backend skeleton doesn't
/// implement model selection yet, so <see cref="DotnetTraitCoverageTests"/>'s dotnet CI leg must
/// skip these; they still run in the main/full CI leg, which always launches the real Python
/// backend. See <see cref="ModelSelectionConformanceFixture"/>/<see cref="ModelDeploymentMapConformanceFixture"/>
/// for why this row set needs two dedicated backend processes.
/// </summary>
public static class ModelSelectionConformanceTestHelpers
{
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Asserts a `/realtime` connect attempt with the given query is rejected with a plain HTTP
    /// 404 BEFORE the WebSocket upgrade -- mirrors
    /// <see cref="PersonaDiscoveryConformanceTests.Realtime_with_an_unknown_persona_is_rejected_with_404_before_the_websocket_opens"/>'s
    /// own raw-<see cref="ClientWebSocket"/> technique, generalised to an arbitrary query string
    /// (persona and/or model) rather than persona alone.
    /// </summary>
    public static async Task AssertRealtimeConnectIs404Async(Uri backendBaseUri, string query, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        var wsUri = new Uri($"ws://{backendBaseUri.Host}:{backendBaseUri.Port}/realtime?{query}");

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(wsUri, ct));
        Assert.Equal(HttpStatusCode.NotFound, socket.HttpStatusCode);
        Assert.NotEqual(WebSocketState.Open, socket.State);
        Assert.NotNull(ex);
    }
}

/// <summary>
/// The four distinct 404 reasons a requested `?model=` can fail for (Rick's PR #106 review item
/// 2's conformance list): unknown, disallowed, undeployed, wrong-pipeline. Each is a DIFFERENT
/// code path in `processors.py` (`dispatch_processor`'s catalog lookup for unknown/wrong-pipeline,
/// `resolve_realtime_model`'s allow-list/deployment checks for disallowed/undeployed) -- see that
/// module's own docstring. Uses <see cref="ModelSelectionConformanceFixture"/> (test-alpha/
/// test-gamma, empty deployment map) throughout: the real `sonic` persona alone can never
/// exercise "disallowed" (it allows both of the real catalog's realtime models), and the empty
/// deployment map is exactly what "undeployed" needs.
/// </summary>
[Collection(ModelSelectionConformanceCollection.Name)]
public sealed class ModelSelectionRejectionConformanceTests(ModelSelectionConformanceFixture fixture)
{
    [Fact]
    public Task Unknown_model_id_is_rejected_with_404() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await ModelSelectionConformanceTestHelpers.AssertRealtimeConnectIs404Async(
            fixture.Backend!.BaseUri, "model=totally-not-a-catalogued-model", ct);
    });

    [Fact]
    public Task Model_not_in_this_personas_own_allowed_list_is_rejected_with_404() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        // gpt-realtime-2.1 IS in the real catalog for the realtime pipeline, but test-gamma's own
        // `models.realtime.allowed` is `["gpt-realtime-mini"]` only -- disallowed, not unknown.
        await ModelSelectionConformanceTestHelpers.AssertRealtimeConnectIs404Async(
            fixture.Backend!.BaseUri,
            $"persona={ModelSelectionConformanceFixture.PersonaGamma}&model=gpt-realtime-2.1", ct);
    });

    [Fact]
    public Task Catalogued_and_allowed_but_undeployed_model_is_rejected_with_404() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        // gpt-realtime-mini is catalogued for realtime AND in test-alpha's own allowed list, but
        // this fixture sets no AZURE_AI_MODEL_DEPLOYMENTS entry for it, and it is NOT test-alpha's
        // own pipeline default (so the AZURE_OPENAI_REALTIME_DEPLOYMENT back-compat fallback does
        // not apply here either) -- undeployed, not disallowed.
        await ModelSelectionConformanceTestHelpers.AssertRealtimeConnectIs404Async(
            fixture.Backend!.BaseUri,
            $"persona={ModelSelectionConformanceFixture.PersonaAlpha}&model=gpt-realtime-mini", ct);
    });

    [Fact]
    public Task Model_catalogued_for_a_different_pipeline_is_rejected_with_404() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        // gpt-5-mini is catalogued for the CASCADE pipeline -- dispatch_processor resolves its
        // pipeline straight from the catalog and finds no processor registered for "cascade" yet
        // (only RTMiddleTier/"realtime" is), 404-ing before resolve_realtime_model's own
        // allow-list check would even run.
        await ModelSelectionConformanceTestHelpers.AssertRealtimeConnectIs404Async(
            fixture.Backend!.BaseUri, "model=gpt-5-mini", ct);
    });
}

/// <summary>
/// The positive-path rows: model list, `?model=` reaching the fake upstream as its own mapped
/// deployment, the omitted-`?model=`-visible-in-metadata row (persona+model+pipeline all
/// present), reasoning sent only for catalog-reasoning models, and `model_mismatch` on resume in
/// both directions. All against the real, shipped `sonic` persona (no persona override needed) --
/// <see cref="ModelDeploymentMapConformanceFixture"/> maps BOTH of sonic's own allowed realtime
/// models (`gpt-realtime-2.1`, its default; `gpt-realtime-mini`, reasoning=false) so neither needs
/// the `AZURE_OPENAI_REALTIME_DEPLOYMENT` back-compat fallback branch.
/// </summary>
[Collection(ModelDeploymentMapConformanceCollection.Name)]
public sealed class ModelSelectionConformanceTests(ModelDeploymentMapConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = ModelSelectionConformanceTestHelpers.FrameTimeout;

    [Fact]
    public Task Api_persona_detail_lists_only_the_selectable_models_shaped_for_the_picker() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/personas/sonic"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var realtime = document.RootElement.GetProperty("models").GetProperty("realtime");
        Assert.Equal("gpt-realtime-2.1", realtime.GetProperty("default").GetString());

        var models = realtime.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(2, models.Length);

        var byId = models.ToDictionary(m => m.GetProperty("id").GetString()!);
        Assert.Equal("GPT Realtime 2.1", byId["gpt-realtime-2.1"].GetProperty("label").GetString());
        Assert.True(byId["gpt-realtime-2.1"].GetProperty("reasoning").GetBoolean());
        Assert.Equal("GPT Realtime mini", byId["gpt-realtime-mini"].GetProperty("label").GetString());
        Assert.False(byId["gpt-realtime-mini"].GetProperty("reasoning").GetBoolean());
    });

    [Fact]
    public Task Explicit_model_reaches_the_fake_upstream_as_its_own_mapped_deployment() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, model: "gpt-realtime-mini", cancellationToken: ct);
        var connection = await connectionTask;

        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        // Rick's PR #106 review item 2: `?model=` must reach the fake upstream as THAT model's OWN
        // AZURE_AI_MODEL_DEPLOYMENTS entry, never the AZURE_OPENAI_REALTIME_DEPLOYMENT back-compat
        // default (which this fixture never even sets to gpt-realtime-mini's mapped name).
        Assert.Equal(ModelDeploymentMapConformanceFixture.RealtimeMiniDeployment, connection!.ModelQueryParam);
    });

    [Fact]
    public Task Omitted_model_binds_to_the_persona_default_visible_alongside_persona_and_pipeline_in_session_metadata() =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);

        Assert.True(metadata is not null, "Expected extension.session_metadata for a fresh session.");
        Assert.Equal("sonic", metadata!.Json.GetProperty("persona").GetString());
        Assert.Equal("gpt-realtime-2.1", metadata.Json.GetProperty("model").GetString());
        Assert.Equal("realtime", metadata.Json.GetProperty("pipeline").GetString());
    });

    [Fact]
    public Task Reasoning_is_sent_only_for_a_catalog_reasoning_model_not_the_other_selectable_one() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var defaultConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using (var defaultBrowser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct))
        {
            var defaultConnection = await defaultConnectionTask;
            Assert.True(defaultConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
            var defaultBootstrap = await defaultConnection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
            Assert.True(defaultBootstrap is not null, "Bootstrap session.update never arrived.");
            Assert.True(defaultBootstrap!.Json.GetProperty("session").TryGetProperty("reasoning", out _),
                "gpt-realtime-2.1 (sonic's own default) is a catalog reasoning model -- `reasoning` must be sent.");
        }

        var miniConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var miniBrowser = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, model: "gpt-realtime-mini", cancellationToken: ct);
        var miniConnection = await miniConnectionTask;
        Assert.True(miniConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        var miniBootstrap = await miniConnection!.ReceivedFrames.WaitForAsync(f => f.Sequence == 0, FrameTimeout, ct);
        Assert.True(miniBootstrap is not null, "Bootstrap session.update never arrived.");
        Assert.False(miniBootstrap!.Json.GetProperty("session").TryGetProperty("reasoning", out _),
            "gpt-realtime-mini is catalogued with reasoning: false -- `reasoning` must never be sent for it, " +
            "regardless of the deployment name it's mapped to.");
    });

    [Fact]
    public Task A_resume_omitting_model_after_binding_to_a_non_default_model_is_rejected_as_model_mismatch() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var first = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, model: "gpt-realtime-mini", cancellationToken: ct);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await first.SendStartSessionAsync(cancellationToken: ct);
        var firstMetadata = await first.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(firstMetadata is not null);
        Assert.Equal("gpt-realtime-mini", firstMetadata!.Json.GetProperty("model").GetString());
        var resumeId = firstMetadata.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrEmpty(resumeId));

        await first.CloseAsync(cancellationToken: ct);
        await first.WaitForCloseAsync(FrameTimeout, ct);
        await first.DisposeAsync();

        // Second connection: `?model=` OMITTED -- resolves to sonic's own default
        // (gpt-realtime-2.1), which is NOT the model the presented resume id was bound to.
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);

        var rejected = await second.ReceivedFrames.WaitForAsync(f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for a model-mismatched resume id.");
        Assert.Equal("model_mismatch", rejected!.Json.GetProperty("reason").GetString());

        var freshMetadata = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected a fresh re-announce after the model_mismatch rejection.");
        Assert.Equal("gpt-realtime-2.1", freshMetadata!.Json.GetProperty("model").GetString());
        Assert.NotEqual(resumeId, freshMetadata.Json.GetProperty("resumeId").GetString());
    });

    [Fact]
    public Task A_resume_with_an_explicit_model_after_binding_to_the_default_is_rejected_as_model_mismatch() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // First connection: `?model=` OMITTED -- binds to sonic's own default (gpt-realtime-2.1).
        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var first = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await first.SendStartSessionAsync(cancellationToken: ct);
        var firstMetadata = await first.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(firstMetadata is not null);
        Assert.Equal("gpt-realtime-2.1", firstMetadata!.Json.GetProperty("model").GetString());
        var resumeId = firstMetadata.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrEmpty(resumeId));

        await first.CloseAsync(cancellationToken: ct);
        await first.WaitForCloseAsync(FrameTimeout, ct);
        await first.DisposeAsync();

        // Second connection: bound to an explicit, DIFFERENT model than the resume id was issued
        // under -- the reverse direction of the mismatch above.
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, model: "gpt-realtime-mini", cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);

        var rejected = await second.ReceivedFrames.WaitForAsync(f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for a model-mismatched resume id.");
        Assert.Equal("model_mismatch", rejected!.Json.GetProperty("reason").GetString());

        var freshMetadata = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected a fresh re-announce after the model_mismatch rejection.");
        Assert.Equal("gpt-realtime-mini", freshMetadata!.Json.GetProperty("model").GetString());
        Assert.NotEqual(resumeId, freshMetadata.Json.GetProperty("resumeId").GetString());
    });
}

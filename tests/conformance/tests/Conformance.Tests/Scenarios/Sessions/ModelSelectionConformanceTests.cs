using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #106 review item 2: conformance rows for issue #75's per-session realtime model
/// selection (design doc sections 5.2/7.3/7.5). S2 part 2 (#12) ports the persona/model HTTP
/// surface and the pre-upgrade `?model=` dispatch/resolution on `/realtime`
/// (Models/ModelDispatch.cs), so <see cref="ModelSelectionRejectionConformanceTests"/>'s four
/// pre-upgrade 404 rows (below) and one HTTP-only positive row in
/// <see cref="ModelSelectionConformanceTests"/> are now tagged. The rest of
/// <see cref="ModelSelectionConformanceTests"/> stays UNTAGGED: those rows need the actual
/// upstream relay to reach `session.created`/forward audio (rtmt.py's `ConnectionForwarder`) --
/// this wave's <see cref="RealtimeProcessor"/> is a deliberate stub (issue #13 lands the real
/// relay), so they still only run against the Python backend. See
/// <see cref="ModelSelectionConformanceFixture"/>/<see cref="ModelDeploymentMapConformanceFixture"/>
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

    /// <summary>
    /// Rick's PR #122 review item 2: the pre-upgrade 404 rows above only assert the STATUS code
    /// (<see cref="ClientWebSocket"/> throws on any non-101 response and never exposes the body).
    /// To read the exact body text -- rtmt.py's `web.Response(status=404, text=...)` shape, which
    /// the C# port must match byte-for-byte, not merely "some 404" -- send the request with plain
    /// <see cref="HttpClient"/> instead, carrying just enough of a WebSocket-upgrade-looking header
    /// set (`Connection: Upgrade`, `Upgrade: websocket`, `Sec-WebSocket-Version`,
    /// `Sec-WebSocket-Key`) that Kestrel's `HttpContext.WebSockets.IsWebSocketRequest` is `true`
    /// (Program.cs's `/realtime` handler 400s immediately otherwise, before persona/model
    /// resolution even runs) -- but since the handler never calls `AcceptWebSocketAsync` on the
    /// rejection paths, Kestrel just writes a normal HTTP response (no actual upgrade handshake),
    /// which <see cref="HttpClient"/> reads like any other response.
    /// </summary>
    public static async Task<string> RealtimeConnectBodyAsync(
        Uri backendBaseUri, string query, HttpStatusCode expectedStatus, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(backendBaseUri, $"/realtime?{query}"));
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        request.Headers.TryAddWithoutValidation(
            "Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(expectedStatus, response.StatusCode);
        return body;
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
[Trait("Dotnet", "ready")]
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

    // Model_catalogued_for_a_different_pipeline_is_rejected_with_404 (a [Trait("Dotnet", "ready")]
    // row needing identical 404 behavior on BOTH backends, using phi-4-mini-local/the local
    // pipeline as its example) was removed by issue #155 (local mode removal, reversing ADR-001
    // decision 7, 2026-09-28): phi-4-mini-local/the local pipeline no longer exists in config.yaml
    // or either backend's catalog, and it was the last model whose pipeline was unregistered
    // identically on both backends. `gpt-5-mini` (cascade) can't fill in for it here either: since
    // #82 registered CascadeProcessor on the Python backend only (the C# backend still has no
    // CascadeProcessor -- see Program.cs), a cascade model now dispatches successfully on Python
    // but still 404s on C#, so the two backends no longer agree on it the way this row requires.
    // There is no longer any catalogued pipeline this dual-backend "unregistered everywhere" case
    // can exercise. `gpt-5-mini`'s Python-side dispatch-success path is separately covered by
    // CascadeConformanceTests.Cascade_dispatch_binds_session_metadata_to_the_requested_persona_model_and_pipeline.

    [Fact]
    public Task Unknown_persona_404_body_is_exactly_the_python_shape() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var body = await ModelSelectionConformanceTestHelpers.RealtimeConnectBodyAsync(
            fixture.Backend!.BaseUri, "persona=totally-not-a-persona", HttpStatusCode.NotFound, ct);
        // rtmt.py's `_websocket_handler`: `web.Response(status=404, text=f"Unknown or disabled
        // persona: '{persona_id}'")` -- plain text, single-quoted id, no trailing punctuation.
        Assert.Equal("Unknown or disabled persona: 'totally-not-a-persona'", body);
    });

    [Fact]
    public Task Unknown_model_404_body_is_exactly_the_python_shape() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var body = await ModelSelectionConformanceTestHelpers.RealtimeConnectBodyAsync(
            fixture.Backend!.BaseUri, "model=totally-not-a-catalogued-model", HttpStatusCode.NotFound, ct);
        // rtmt.py's `dispatch_processor`/`resolve_realtime_model` except-clauses both return the
        // identical fixed body regardless of the underlying reason (Rick's PR #122 review item
        // 2): `web.Response(status=404, text=f"Unknown or disallowed model: {model_id!r}")`.
        // `!r` on a `str` is single-quoted Python repr -- `PyRepr` in Program.cs mirrors this.
        //
        // The bare `None` form (`Unknown or disallowed model: None`, for an omitted `?model=`
        // that still gets rejected) is Python's shape for the case where `requested_model_id` is
        // `None` -- but that case is provably UNREACHABLE at runtime on either backend: omitting
        // `?model=` always resolves to the PERSONA'S OWN pipeline default, and both backends'
        // startup-time `ValidatePersonaDefaults`/`validate_persona_defaults` gate refuses to start
        // at all if any enabled persona's own default isn't catalogued for its own pipeline (which
        // has a registered processor) and deployed (via the back-compat fallback for defaults) --
        // so a running backend can never reach either except-clause with `requested_model_id is
        // None`. No conformance row exists for it for that reason; this is not a coverage gap.
        Assert.Equal("Unknown or disallowed model: 'totally-not-a-catalogued-model'", body);
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
    [Trait("Dotnet", "ready")]
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

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// persona-binding contract -- design doc section 5.2. S2 part 2 (#12) ports the persona
/// discovery/detail HTTP routes and the pre-upgrade ?persona= validation on /realtime, so four of
/// the five rows below are tagged <c>[Trait("Dotnet", "ready")]</c>.
/// <see cref="Omitted_persona_binds_to_the_default_persona_visible_in_session_metadata"/> stays
/// UNTAGGED: it needs the real upstream relay to actually reach `session.created` and echo
/// `extension.session_metadata` (rtmt.py's `_websocket_handler`/`ConnectionForwarder`) --
/// <see cref="RealtimeProcessor"/> this wave is a deliberate stub (issue #13 lands the real
/// relay), so this row still only runs against the Python backend.
///
/// Rick's PR #120 review round 2, required item 4: <c>Api_persona_detail_returns_200_for_the_default_persona</c>
/// now also pins <c>roleName</c> on the wire (design doc section 5.2). PR #122 merged its C#
/// persona surface first, but its <c>PersonaRoutes.BuildPersonaDetailBody</c> didn't emit
/// <c>roleName</c> yet; this PR adds it there too, so the row keeps its inherited
/// <c>[Trait("Dotnet", "ready")]</c> tag -- it now passes on both backends.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class PersonaDiscoveryConformanceTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Api_personas_returns_the_designed_shape() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/personas"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = document.RootElement;

        Assert.Equal("sonic", root.GetProperty("default").GetString());

        var personas = root.GetProperty("personas");
        Assert.True(personas.GetArrayLength() > 0, "Expected at least one persona summary.");
        var sonic = personas.EnumerateArray().First(p => p.GetProperty("id").GetString() == "sonic");
        Assert.False(string.IsNullOrWhiteSpace(sonic.GetProperty("displayName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(sonic.GetProperty("logoUrl").GetString()));
        Assert.Equal(JsonValueKind.Object, sonic.GetProperty("theme").ValueKind);

        // Rick's PR #102 review item 3: backends[].url is each backend's PUBLIC BASE URL (not
        // "/realtime"), built from BACKEND_URI/BACKEND_DOTNET_URI (#93). Whether either backend
        // entry is present at all depends on the harness's own env, so this only asserts the
        // shape of whichever entries do exist, never their count.
        var backends = root.GetProperty("backends");
        foreach (var backend in backends.EnumerateArray())
        {
            var url = backend.GetProperty("url").GetString() ?? "";
            Assert.DoesNotContain("/realtime", url, StringComparison.Ordinal);
        }
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Api_persona_detail_returns_200_for_the_default_persona() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/personas/sonic"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        Assert.Equal("sonic", document.RootElement.GetProperty("id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("menuUrl").GetString()));

        // Rick's #120 review round 2, required item 4: `roleName` is a new field on the pinned
        // wire contract (design doc section 5.2) -- read the expected value from Sonic's OWN
        // persona.json (never a literal) so this row can't silently drift from the pack it proves.
        var personaJsonPath = Path.Combine(fixture.PersonasDirectory, "sonic", "persona.json");
        using var personaDocument = JsonDocument.Parse(await File.ReadAllTextAsync(personaJsonPath, ct));
        var expectedRoleName = personaDocument.RootElement.GetProperty("roleName").GetString();
        Assert.Equal(expectedRoleName, document.RootElement.GetProperty("roleName").GetString());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Api_persona_detail_returns_404_for_an_unknown_id() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/personas/nope"), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Realtime_with_an_unknown_persona_is_rejected_with_404_before_the_websocket_opens() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = fixture.Backend!.BaseUri;

        // rtmt.py's _websocket_handler resolves and validates ?persona= BEFORE `ws.prepare()`
        // (the WebSocket upgrade) runs -- a plain HTTP 404 response, never a WS-level close code.
        // Mirrors Scenarios/Security/OriginValidationTests.cs's own raw-ClientWebSocket technique
        // for asserting a pre-upgrade HTTP rejection.
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        var wsUri = new Uri($"ws://{backend.Host}:{backend.Port}/realtime?persona=nope");

        var ex = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(wsUri, ct));
        Assert.Equal(HttpStatusCode.NotFound, socket.HttpStatusCode);
        Assert.NotEqual(WebSocketState.Open, socket.State);
        Assert.NotNull(ex);
    });

    [Fact]
    public Task Omitted_persona_binds_to_the_default_persona_visible_in_session_metadata() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // No `persona:` argument at all -- exactly the omitted-`?persona=` case (design doc 5.2 /
        // Rick's PR #102 review item 4: session metadata must carry the bound persona id).
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);

        Assert.True(metadata is not null, "Expected extension.session_metadata for a fresh session.");
        Assert.Equal("sonic", metadata!.Json.GetProperty("persona").GetString());
    });
}

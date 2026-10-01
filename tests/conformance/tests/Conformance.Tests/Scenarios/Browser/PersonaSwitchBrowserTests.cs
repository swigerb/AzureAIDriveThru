using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Conformance.Tests.Scenarios.Browser;

/// <summary>
/// Issue #171 round 2 (Rick's PR #173 round-1 review item 4 -- REQUIRED). The production bug is
/// a timing race across three independently-clocked things: a real browser's native WebSocket
/// open/close events, React's async <c>/api/personas/{id}</c> fetch, and the server's own close
/// for the outgoing <c>extension.end_session</c>. <c>useRealtime.socketRace.test.tsx</c>'s
/// hand-built <see cref="object">FakeWebSocket</see> proves the hook's guards hold against a
/// controlled clock in every ordering this change anticipated -- but only a REAL browser driving
/// the REAL built frontend (served by a REAL Python backend) against the fake realtime upstream
/// can prove the fix survives genuine, unscripted network/event-loop timing, which is exactly why
/// Rick's review calls this leg required rather than optional.
///
/// Scenario (both cases share <see cref="RunSwitchScenarioAsync"/>, varying only how soon "Start
/// recording" is clicked after the persona-picker selection):
///  - Load <c>?persona=test-alpha</c>; wait for the first upstream connection and the frontend's
///    own <c>extension.session_metadata</c> frame on its first socket.
///  - Switch to test-beta through the REAL <c>PersonaPicker</c> &lt;select&gt;
///    (<c>aria-label="Select persona"</c>), never by changing the URL or calling a hook directly.
///  - Case A (settled): wait until the newest captured socket's URL carries
///    <c>persona=test-beta</c> and is OPEN, THEN click "Start recording".
///  - Case B (immediate): click "Start recording" right after the picker selection resolves, with
///    no wait at all -- this is the H1/H1b window where the replacement socket may still be
///    CONNECTING (or may not even exist as a JS object yet, if the persona fetch itself hasn't
///    resolved).
/// Both cases assert, against the SAME five properties Rick's review lists: the fake upstream
/// connection for the test-beta session receives the client's <c>session.update</c>
/// (<c>turn_detection.type=="server_vad"</c>, <c>threshold==0.7</c>); the greeting the server
/// sends upstream for that connection contains
/// <see cref="PersonaSmokeExpectations.For"/>("test-beta").GreetingSubstring; the browser sent
/// exactly one <c>session.update</c> after the switch, on the test-beta socket; no test-alpha
/// socket was created after the switch click; and no frame other than
/// <c>extension.end_session</c> ever landed on the original test-alpha socket after the click.
///
/// Reuses the <c>window.__sockets</c> capture pattern from <see cref="OrderResumeBrowserTests"/>
/// (kept as its own copy here per that file's own precedent: same shape, different scenario, and
/// this suite's collection/fixture -- <see cref="PersonaSwitchBrowserFixture"/> -- is deliberately
/// a different one so Personas/Persona/PersonasDir can point at the test-alpha/test-beta fixture
/// pack, which the shared <see cref="BrowserConformanceCollection"/> fixture does not).
/// </summary>
[Collection(PersonaSwitchBrowserCollection.Name)]
[Trait("Category", "Browser")]
public sealed class PersonaSwitchBrowserTests(PersonaSwitchBrowserFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    // Same shape as OrderResumeBrowserTests' own InitScript, trimmed to just the parts this
    // scenario needs (no drop/probe helpers): every /realtime socket's URL and sent/received
    // frames, captured from the page itself so .NET can inspect them without reaching into the
    // frontend's own React state.
    private const string InitScript = """
    (() => {
      window.__sockets = [];
      const OrigWS = window.WebSocket;
      window.WebSocket = new Proxy(OrigWS, {
        construct(target, args) {
          const ws = new target(...args);
          if (String(args[0] ?? '').includes('/realtime')) {
            const rec = { url: String(args[0]), sent: [], received: [], ws };
            const origSend = ws.send.bind(ws);
            ws.send = (data) => {
              try { rec.sent.push(JSON.parse(data)); } catch { rec.sent.push({ type: '<binary>' }); }
              return origSend(data);
            };
            ws.addEventListener('message', (ev) => {
              try { rec.received.push(JSON.parse(ev.data)); } catch { rec.received.push({ type: '<binary>' }); }
            });
            window.__sockets.push(rec);
          }
          return ws;
        }
      });
    })();
    """;

    private static Task<int> SocketCountAsync(IPage page) => page.EvaluateAsync<int>("window.__sockets.length");

    private static Task<JsonElement> ReceivedAsync(IPage page, int index) =>
        page.EvaluateAsync<JsonElement>($"window.__sockets[{index}] ? window.__sockets[{index}].received : []");

    private static Task<JsonElement> SentAsync(IPage page, int index) =>
        page.EvaluateAsync<JsonElement>($"window.__sockets[{index}] ? window.__sockets[{index}].sent : []");

    private static Task<string> UrlAsync(IPage page, int index) =>
        page.EvaluateAsync<string>($"window.__sockets[{index}] ? window.__sockets[{index}].url : ''");

    private static Task<int> ReadyStateAsync(IPage page, int index) =>
        page.EvaluateAsync<int>($"window.__sockets[{index}] ? window.__sockets[{index}].ws.readyState : -1");

    private static IEnumerable<string> TypesOf(JsonElement frames) =>
        frames.EnumerateArray().Select(f => f.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "");

    private static bool HasSessionMetadata(JsonElement received) =>
        TypesOf(received).Any(t => t == "extension.session_metadata");

    private static bool IsMessageItem(RecordedFrame f) =>
        f.Type == "conversation.item.create" &&
        f.Json.TryGetProperty("item", out var item) &&
        item.TryGetProperty("type", out var itemType) && itemType.GetString() == "message";

    /// <summary>Polls <paramref name="probe"/> until <paramref name="ready"/> is satisfied or
    /// <paramref name="timeout"/> elapses -- same black-box polling helper as
    /// <see cref="OrderResumeBrowserTests"/>'s own copy, since none of the state this suite reads
    /// (socket count/url/readyState, captured frames) is push-notified to a .NET caller.</summary>
    private static async Task<T> UntilAsync<T>(
        Func<Task<T>> probe, Func<T, bool> ready, TimeSpan timeout, string what, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var value = await probe().ConfigureAwait(false);
            if (ready(value))
            {
                return value;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for {what}.");
            }
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Finds the lowest-indexed captured socket at or after <paramref name="fromIndex"/>
    /// whose URL carries <c>persona=&lt;personaId&gt;</c>, or -1 if none exists yet.</summary>
    private static async Task<int> FindPersonaSocketIndexAsync(IPage page, string personaId, int fromIndex, CancellationToken ct)
    {
        var count = await SocketCountAsync(page).ConfigureAwait(false);
        for (var i = fromIndex; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var url = await UrlAsync(page, i).ConfigureAwait(false);
            if (url.Contains($"persona={personaId}", StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task<(IBrowserContext Context, IPage Page)> NewPageAsync(IBrowser browser, CancellationToken ct)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { Permissions = ["microphone"] })
            .ConfigureAwait(false);
        await context.AddInitScriptAsync(InitScript).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        return (context, page);
    }

    /// <summary>
    /// Shared scenario body for both cases. <paramref name="clickImmediately"/> selects Case B
    /// (click "Start recording" right after the picker selection resolves, no settling wait) vs
    /// Case A (wait for the test-beta socket to exist and be OPEN first).
    /// </summary>
    private async Task RunSwitchScenarioAsync(bool clickImmediately)
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, page) = await NewPageAsync(fixture.Browser!, ct).ConfigureAwait(false);
        await using var _ = context;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await page.GotoAsync($"{fixture.Backend!.BaseUri}?persona={PersonaSwitchBackendFixture.PersonaA}").ConfigureAwait(false);
        var firstConnection = await firstConnectionTask;
        Assert.True(firstConnection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await UntilAsync(() => SocketCountAsync(page), n => n >= 1, FrameTimeout, "the frontend's first realtime socket to open", ct);
        await UntilAsync(
            () => ReceivedAsync(page, 0),
            HasSessionMetadata,
            FrameTimeout, "extension.session_metadata on the test-alpha socket", ct);

        var socketsBeforeSwitch = await SocketCountAsync(page);
        var alphaSentBeforeSwitch = (await SentAsync(page, 0)).GetArrayLength();

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);

        // The real PersonaPicker <select>, exactly as a guest would operate it -- never the URL
        // or a hook called directly.
        await page.GetByLabel("Select persona").SelectOptionAsync(
            new SelectOptionValue { Value = PersonaSwitchBackendFixture.PersonaB }).ConfigureAwait(false);

        if (!clickImmediately)
        {
            // Case A (settled): wait until the newest socket is bound to test-beta and OPEN
            // before tapping, so this case proves the switch works in the "everything already
            // settled" ordering too, not just the race.
            var settledIndex = await UntilAsync(
                () => FindPersonaSocketIndexAsync(page, PersonaSwitchBackendFixture.PersonaB, socketsBeforeSwitch, ct),
                idx => idx >= 0, FrameTimeout, "a test-beta socket to be created", ct);
            await UntilAsync(() => ReadyStateAsync(page, settledIndex), state => state == 1 /* OPEN */,
                FrameTimeout, "the test-beta socket to open", ct);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);

        // ── The fake upstream connection for the test-beta session ──
        var secondConnection = await secondConnectionTask;
        Assert.True(secondConnection is not null, $"No upstream connection for the switched-to persona was accepted within {FrameTimeout}.");

        var sessionUpdate = await secondConnection!.ReceivedFrames.WaitForAsync(f => f.Type == "session.update", FrameTimeout, ct);
        Assert.True(sessionUpdate is not null, "Expected the client's session.update on the test-beta upstream connection.");
        // rtmt.py's `_to_ga_session` moves the legacy top-level `turn_detection` the browser
        // sends into the GA endpoint's `session.audio.input.turn_detection` before relaying it
        // upstream -- see `_to_ga_session`'s docstring and `_BOOTSTRAP_CLIENT_SESSION`.
        var turnDetection = sessionUpdate!.Json.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("turn_detection");
        Assert.Equal("server_vad", turnDetection.GetProperty("type").GetString());
        Assert.Equal(0.7, turnDetection.GetProperty("threshold").GetDouble());

        var greetingFrame = await secondConnection.ReceivedFrames.WaitForAsync(IsMessageItem, FrameTimeout, ct);
        Assert.True(greetingFrame is not null, "Expected the test-beta greeting conversation.item.create on the test-beta upstream connection.");
        var greetingText = greetingFrame!.Json.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains(PersonaSmokeExpectations.For(PersonaSwitchBackendFixture.PersonaB).GreetingSubstring, greetingText);

        // ── Browser side: exactly one socket for test-beta, exactly one session.update on it ──
        var betaIndex = await UntilAsync(
            () => FindPersonaSocketIndexAsync(page, PersonaSwitchBackendFixture.PersonaB, socketsBeforeSwitch, ct),
            idx => idx >= 0, FrameTimeout, "a test-beta socket to exist", ct);
        var betaSent = await UntilAsync(
            () => SentAsync(page, betaIndex),
            sent => TypesOf(sent).Count(t => t == "session.update") >= 1,
            FrameTimeout, "session.update to be sent on the test-beta socket", ct);
        Assert.Equal(1, TypesOf(betaSent).Count(t => t == "session.update"));

        // A little settling margin before the negative assertions below: both the
        // double-connect bug (H2) and a stray-frame regression (H3) would already have
        // manifested well within this window if they were going to at all.
        await Task.Delay(300, ct).ConfigureAwait(false);

        // ── No orphaned test-alpha socket after the switch click ──
        var socketsAfter = await SocketCountAsync(page);
        for (var i = socketsBeforeSwitch; i < socketsAfter; i++)
        {
            var url = await UrlAsync(page, i);
            Assert.False(
                url.Contains($"persona={PersonaSwitchBackendFixture.PersonaA}", StringComparison.Ordinal),
                $"Socket #{i} (url={url}) was created for test-alpha AFTER the switch click -- orphaned double-connect (issue #171 round 2, H2).");
        }

        // ── No stray frame on the OLD (test-alpha) socket after the click, other than the
        //    extension.end_session the switch itself sends ──
        var alphaSentAfter = await SentAsync(page, 0);
        var alphaTypesAfter = TypesOf(alphaSentAfter).Skip(alphaSentBeforeSwitch).ToList();
        Assert.True(
            alphaTypesAfter.All(t => t == "extension.end_session"),
            "Expected only extension.end_session on the test-alpha socket after the switch click, " +
            $"got: [{string.Join(", ", alphaTypesAfter)}].");
    }

    [Fact]
    public Task Settled_tap_after_a_persona_switch_sends_session_update_and_gets_the_new_personas_greeting() =>
        fixture.RunAsync(() => RunSwitchScenarioAsync(clickImmediately: false));

    [Fact]
    public Task Immediate_tap_right_after_a_persona_switch_still_sends_session_update_and_gets_the_new_personas_greeting() =>
        fixture.RunAsync(() => RunSwitchScenarioAsync(clickImmediately: true));
}

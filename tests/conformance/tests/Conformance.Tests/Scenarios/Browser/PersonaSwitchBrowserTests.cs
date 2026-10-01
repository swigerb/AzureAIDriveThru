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
/// Six cases, all sharing <see cref="RunSwitchScenarioAsync"/> or
/// <see cref="AssertSwitchDeliveredToNewPersonaAsync"/>'s common setup/assertions:
///  - Load <c>?persona=test-alpha</c>; wait for the first upstream connection and the frontend's
///    own <c>extension.session_metadata</c> frame on its first socket.
///  - Switch to test-beta through the REAL <c>PersonaPicker</c> &lt;select&gt;
///    (<c>aria-label="Select persona"</c>), never by changing the URL or calling a hook directly.
///
/// Cases A and B are end-to-end SMOKE tests for switch-then-talk -- they exercise real,
/// unscripted browser timing but do not reliably land inside the race window (they pass on
/// dev's unmodified hook too), so they cannot by themselves prove H2/H4 are fixed:
///  - Case A (settled): wait until the newest captured socket's URL carries
///    <c>persona=test-beta</c> and is OPEN, THEN click "Start recording".
///  - Case B (immediate): click "Start recording" right after the picker selection resolves, with
///    no wait at all -- this is the H1/H1b window where the replacement socket may still be
///    CONNECTING (or may not even exist as a JS object yet, if the persona fetch itself hasn't
///    resolved).
///
/// Cases C and D (issue #171 round 3, Rick's round-2 review item 2) are the DETERMINISTIC
/// regression detectors -- each deliberately forces the browser into one specific, previously
/// unreachable-by-chance ordering of the race, and each is confirmed (by Rick's own scratch runs)
/// to fail against dev's unmodified hook (built from <c>ebb79ec</c>) and pass once H2/H4 are
/// fixed:
///  - Case C (<see cref="RunSwitchScenarioCAsync"/>): holds the <c>/api/personas/test-beta</c>
///    detail fetch so the guest's tap on "Start recording" lands squarely inside the
///    pending-switch window, after the old socket's close has already landed but before the new
///    persona's identity has changed -- the exact window a bare <c>reconnect()</c> has nothing
///    but a genuinely CLOSED <c>readyState</c> to go on (H2/H4).
///  - Case D (<see cref="RunSwitchScenarioDAsync"/>): holds the OLD socket's own
///    <c>onclose</c> HANDLER INVOCATION (not its native <c>readyState</c>, which transitions to
///    CLOSED as soon as the server's close frame lands regardless) past the point where the NEW
///    persona's socket has already opened -- the production bug's actual ordering, where the
///    hook's own close-handling logic runs well after the replacement socket is already live.
/// H1 proper (the old close delivered after the new socket is created but before it opens) cannot
/// be held in-page this way: by the time a held <c>onopen</c> fires, the native
/// <c>readyState</c> is already OPEN, so the library would send regardless of what this suite
/// holds. That ordering is left to the FakeWebSocket unit tests in
/// <c>useRealtime.socketRace.test.tsx</c>, which control a synthetic clock precisely enough to
/// hold it.
///
/// Cases E and E2 (issue #171 round 4, Rick's round-3 review item 3 -- H5) cover a DIFFERENT
/// precondition than C/D: the socket is already CLOSED on purpose (here, the backend's own
/// idle-timeout sweep) BEFORE the guest ever touches the picker, so there is no background
/// reconnect timer of any kind already primed for the switch to piggyback on:
///  - Case E (<see cref="RunSwitchScenarioEAsync"/> with <c>holdFetch: false</c>): waits for the
///    first socket's native <c>readyState</c> to reach CLOSED via the idle sweep, then switches
///    and taps. Passes on dev's unmodified hook, fails at the round-3 head.
///  - Case E2 (same method, <c>holdFetch: true</c>): Case C's held-fetch gate layered on top of
///    the same idle-closed precondition, so the tap lands before the persona fetch resolves
///    either way. Fails on BOTH dev and the round-3 head; must pass once H5 (item 1) lands.
///
/// All six cases assert, against the SAME five properties Rick's review lists: the fake upstream
/// connection for the test-beta session receives the client's <c>session.update</c>
/// (<c>turn_detection.type=="server_vad"</c>, <c>threshold==0.7</c>); the greeting the server
/// sends upstream for that connection contains
/// <see cref="PersonaSmokeExpectations.For"/>("test-beta").GreetingSubstring; the browser created
/// EXACTLY ONE socket for test-beta and sent <c>session.update</c> on it EXACTLY ONCE; no
/// test-alpha socket was created after the switch click; and no frame other than
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
            const rec = { url: String(args[0]), sent: [], received: [], ws, holdClose: false, heldClose: null };
            const origSend = ws.send.bind(ws);
            ws.send = (data) => {
              try { rec.sent.push(JSON.parse(data)); } catch { rec.sent.push({ type: '<binary>' }); }
              return origSend(data);
            };
            ws.addEventListener('message', (ev) => {
              try { rec.received.push(JSON.parse(ev.data)); } catch { rec.received.push({ type: '<binary>' }); }
            });
            // Case D (issue #171 round 3): lets a test hold this socket's own `onclose` HANDLER
            // INVOCATION past some later point -- not its native `readyState`, which still
            // transitions to CLOSED the instant the server's close frame lands, regardless of
            // this interception. react-use-websocket 4.13.0 assigns
            // `webSocketInstance.onclose = ...` in attach-listener.js, so this wraps exactly the
            // handler under test.
            const cdesc = Object.getOwnPropertyDescriptor(OrigWS.prototype, 'onclose');
            Object.defineProperty(ws, 'onclose', { configurable: true,
              get() { return cdesc.get.call(ws); },
              set(fn) { cdesc.set.call(ws, fn ? ((ev) => { if (rec.holdClose) { rec.heldClose = () => fn.call(ws, ev); } else { fn.call(ws, ev); } }) : fn); } });
            window.__sockets.push(rec);
          }
          return ws;
        }
      });
      // Releases a held `onclose` invocation recorded above (Case D) for socket index `i`, if any.
      window.__releaseClose = (i) => {
        const r = window.__sockets[i];
        r.holdClose = false;
        if (r.heldClose) { const f = r.heldClose; r.heldClose = null; f(); }
      };
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

        var secondConnection = await secondConnectionTask;
        await AssertSwitchDeliveredToNewPersonaAsync(page, socketsBeforeSwitch, alphaSentBeforeSwitch, secondConnection, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The five properties Rick's round-1 and round-2 reviews both list, shared by every case
    /// (A/B smoke, C/D deterministic detectors): the fake upstream connection for the
    /// switched-to persona received the client's <c>session.update</c> and that persona's
    /// greeting; the browser created EXACTLY ONE socket for the new persona and sent
    /// <c>session.update</c> on it EXACTLY ONCE; no socket for the OLD persona was created after
    /// the switch click; and no frame other than <c>extension.end_session</c> ever landed on the
    /// original OLD-persona socket after the click.
    /// </summary>
    private async Task AssertSwitchDeliveredToNewPersonaAsync(
        IPage page,
        int socketsBeforeSwitch,
        int alphaSentBeforeSwitch,
        FakeRealtimeConnection? secondConnection,
        CancellationToken ct)
    {
        // ── The fake upstream connection for the test-beta session ──
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

        // ── Browser side: EXACTLY ONE socket for test-beta, session.update on it EXACTLY ONCE ──
        var betaIndex = await UntilAsync(
            () => FindPersonaSocketIndexAsync(page, PersonaSwitchBackendFixture.PersonaB, socketsBeforeSwitch, ct),
            idx => idx >= 0, FrameTimeout, "a test-beta socket to exist", ct);
        var betaSent = await UntilAsync(
            () => SentAsync(page, betaIndex),
            sent => TypesOf(sent).Count(t => t == "session.update") >= 1,
            FrameTimeout, "session.update to be sent on the test-beta socket", ct);
        Assert.Equal(1, TypesOf(betaSent).Count(t => t == "session.update"));

        // A little settling margin before the negative assertions below: both the
        // double-connect bug (H2/H4) and a stray-frame regression (H3) would already have
        // manifested well within this window if they were going to at all.
        await Task.Delay(300, ct).ConfigureAwait(false);

        // ── No orphaned test-alpha socket after the switch click, and exactly one test-beta
        //    socket overall (issue #171 round 3: the earlier version of this check only counted
        //    session.update on the FIRST test-beta socket found, which would have missed a
        //    second, orphaned one existing alongside it) ──
        var socketsAfter = await SocketCountAsync(page);
        var betaSocketCount = 0;
        for (var i = socketsBeforeSwitch; i < socketsAfter; i++)
        {
            var url = await UrlAsync(page, i);
            Assert.False(
                url.Contains($"persona={PersonaSwitchBackendFixture.PersonaA}", StringComparison.Ordinal),
                $"Socket #{i} (url={url}) was created for test-alpha AFTER the switch click -- orphaned double-connect (issue #171, H2/H4).");
            if (url.Contains($"persona={PersonaSwitchBackendFixture.PersonaB}", StringComparison.Ordinal))
            {
                betaSocketCount++;
            }
        }
        Assert.Equal(1, betaSocketCount);

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

    /// <summary>
    /// Case C (issue #171 round 3, Rick's round-2 review item 2): holds the
    /// <c>/api/personas/test-beta</c> detail fetch so the guest's tap on "Start recording" lands
    /// squarely inside the pending-switch window -- after the server's close for the outgoing
    /// <c>extension.end_session</c> has already landed (native <c>readyState === 3</c>, CLOSED)
    /// but before the persona fetch (and therefore the identity props `useWebSocket` keys its own
    /// url on) has resolved either way. This is exactly the window a bare <c>reconnect()</c> has
    /// nothing but a genuinely CLOSED <c>readyState</c> to go on -- the H2/H4 window. Rick's own
    /// runs: fails 3/3 against dev's unmodified hook (H2 orphan) and fails 3/3 at the pre-H4-fix
    /// head (the next upstream connection is test-alpha's, greeting substring not found). Must
    /// pass once the fix (item 1) lands.
    /// </summary>
    private async Task RunSwitchScenarioCAsync()
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

        // Hold the persona-detail fetch BEFORE the picker selection: the selection itself (and
        // the endSession({switching:true})/server-close dance it triggers) still runs, but the
        // prop change that would make useWebSocket's own url-keyed effect open the replacement
        // socket never fires until `gate` is released below.
        var gate = new TaskCompletionSource();
        await page.RouteAsync($"**/api/personas/{PersonaSwitchBackendFixture.PersonaB}", async route =>
        {
            await gate.Task.ConfigureAwait(false);
            await route.ContinueAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);

        // The real PersonaPicker <select>, exactly as a guest would operate it.
        await page.GetByLabel("Select persona").SelectOptionAsync(
            new SelectOptionValue { Value = PersonaSwitchBackendFixture.PersonaB }).ConfigureAwait(false);

        // Wait until the server's own close for extension.end_session has actually landed -- the
        // exact moment onToggleListening's `if (!isConnected) reconnect()` branch would see a
        // genuinely CLOSED readyState on the OLD socket, with the fetch still held pending.
        await UntilAsync(() => ReadyStateAsync(page, 0), state => state == 3 /* CLOSED */,
            FrameTimeout, "the test-alpha socket's close (the server's 1000 session_ended) to land", ct);

        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);

        // Long enough for a wrong (pre-fix) socket to be created AND to open on loopback, if the
        // bare-reconnect()-on-CLOSED bug is present -- Rick's own runs confirm this window is
        // sufficient to expose it.
        await Task.Delay(1000, ct).ConfigureAwait(false);
        gate.SetResult();

        var secondConnection = await secondConnectionTask;
        await AssertSwitchDeliveredToNewPersonaAsync(page, socketsBeforeSwitch, alphaSentBeforeSwitch, secondConnection, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Case D (issue #171 round 3, Rick's round-2 review item 2): holds the OLD socket's own
    /// <c>onclose</c> HANDLER INVOCATION (via the <see cref="InitScript"/>'s property-descriptor
    /// interception -- not its native <c>readyState</c>, which still transitions to CLOSED the
    /// instant the server's close frame lands, regardless) past the point where the NEW persona's
    /// socket has already opened. This is the production bug's actual ordering: the hook's own
    /// close-handling logic (where <c>switchingRef</c>/<c>endingRef</c> bookkeeping lives) can run
    /// well after the replacement socket is already live, not only before it. Rick's own runs:
    /// fails 3/3 against dev's unmodified hook (built from <c>ebb79ec</c>'s <c>useRealtime.tsx</c>
    /// and <c>App.tsx</c>) and passes 3/3 once the fix (item 1) lands.
    /// </summary>
    private async Task RunSwitchScenarioDAsync()
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

        // Hold socket 0's (test-alpha's) own onclose HANDLER INVOCATION before the switch even
        // starts, so the server's close for extension.end_session (sent moments later) is
        // captured but deferred.
        await page.EvaluateAsync("window.__sockets[0].holdClose = true").ConfigureAwait(false);

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);

        await page.GetByLabel("Select persona").SelectOptionAsync(
            new SelectOptionValue { Value = PersonaSwitchBackendFixture.PersonaB }).ConfigureAwait(false);

        // Wait until the replacement (test-beta) socket exists and is fully OPEN -- entirely
        // independent of the still-deferred old-socket close, since react-use-websocket's
        // url-keyed effect reacts to the prop change itself, not to the old socket's close.
        var betaIndex = await UntilAsync(
            () => FindPersonaSocketIndexAsync(page, PersonaSwitchBackendFixture.PersonaB, socketsBeforeSwitch, ct),
            idx => idx >= 0, FrameTimeout, "a test-beta socket to be created", ct);
        await UntilAsync(() => ReadyStateAsync(page, betaIndex), state => state == 1 /* OPEN */,
            FrameTimeout, "the test-beta socket to open", ct);

        // Only now -- after the replacement socket is already live -- let the OLD socket's
        // deferred close handler run: exactly the production bug's ordering.
        await page.EvaluateAsync("window.__releaseClose(0)").ConfigureAwait(false);

        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);

        var secondConnection = await secondConnectionTask;
        await AssertSwitchDeliveredToNewPersonaAsync(page, socketsBeforeSwitch, alphaSentBeforeSwitch, secondConnection, ct).ConfigureAwait(false);
    }

    [Fact]
    public Task Held_persona_fetch_during_the_pending_switch_window_still_delivers_only_to_the_new_persona() =>
        fixture.RunAsync(RunSwitchScenarioCAsync);

    [Fact]
    public Task Held_old_close_past_the_new_sockets_open_still_delivers_only_to_the_new_persona() =>
        fixture.RunAsync(RunSwitchScenarioDAsync);

    /// <summary>
    /// Case E / E2 (issue #171 round 4, Rick's round-3 review item 3 -- REQUIRED, H5): Cases A
    /// through D above all start a switch while the OLD socket is still live, or at worst mid-way
    /// through the server's close for our own <c>extension.end_session</c> -- in every one of
    /// those orderings a background reconnect timer or the url-keyed effect is already primed to
    /// fire once the right thing changes. H5 is a DIFFERENT precondition entirely: the socket is
    /// already CLOSED on purpose (here, the backend's own idle-timeout sweep --
    /// <c>CONFORMANCE_IDLE_TIMEOUT_SECONDS=10</c> on this fixture's
    /// <c>BackendProfiles.BrowserTimersDevelopment</c> profile, same as
    /// <see cref="OrderResumeBrowserTests.Idle_close_does_not_reconnect_a_tap_starts_a_fresh_session"/>'s
    /// own idle wait) BEFORE the guest ever touches the picker, so <c>shouldConnect</c> is already
    /// false and there is no pending reconnect timer of any kind for the switch to piggyback on.
    /// Without the fix, nothing -- not even a later mic tap -- ever reopens a socket at all; only
    /// "New order" recovers. Rick's own scratch runs: Case E passes 3/3 against dev's unmodified
    /// hook and fails 3/3 at the round-3 head ("No upstream connection for the switched-to persona
    /// was accepted within 00:00:30"); Case E2 (Case C's held-fetch gate layered on top of the
    /// same idle-closed precondition, so the tap lands before the persona fetch resolves either
    /// way) fails 3/3 on BOTH dev and the round-3 head. Both must pass 3/3 once the fix (item 1)
    /// lands.
    /// </summary>
    private async Task RunSwitchScenarioEAsync(bool holdFetch)
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

        // Let the first socket run all the way down to the backend's own idle-timeout sweep --
        // by the time the guest ever touches the picker below, `shouldConnect` is already false
        // and there is no background reconnect timer pending, unlike every other case above.
        await UntilAsync(() => ReadyStateAsync(page, 0), state => state == 3 /* CLOSED */,
            TimeSpan.FromSeconds(40), "the test-alpha socket's own idle-timeout close to land", ct);

        TaskCompletionSource? gate = null;
        if (holdFetch)
        {
            // Case E2: Case C's own held-fetch gate, layered on top of the already-idle-closed
            // precondition -- the tap must land before the persona fetch (and therefore the prop
            // change the H5 fix's useEffect keys on) resolves either way.
            gate = new TaskCompletionSource();
            var localGate = gate;
            await page.RouteAsync($"**/api/personas/{PersonaSwitchBackendFixture.PersonaB}", async route =>
            {
                await localGate.Task.ConfigureAwait(false);
                await route.ContinueAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);

        // The real PersonaPicker <select>, exactly as a guest would operate it -- never the URL
        // or a hook called directly.
        await page.GetByLabel("Select persona").SelectOptionAsync(
            new SelectOptionValue { Value = PersonaSwitchBackendFixture.PersonaB }).ConfigureAwait(false);

        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);

        if (holdFetch)
        {
            // Long enough for the tap above to land, and -- without the fix -- for an orphaned
            // switchingRef to prove it never recovers on its own, before letting the fetch resolve.
            await Task.Delay(1000, ct).ConfigureAwait(false);
            gate!.SetResult();
        }

        var secondConnection = await secondConnectionTask;
        await AssertSwitchDeliveredToNewPersonaAsync(page, socketsBeforeSwitch, alphaSentBeforeSwitch, secondConnection, ct).ConfigureAwait(false);
    }

    [Fact]
    public Task Idle_closed_socket_then_persona_switch_then_tap_recovers_the_new_personas_session() =>
        fixture.RunAsync(() => RunSwitchScenarioEAsync(holdFetch: false));

    [Fact]
    public Task Idle_closed_socket_then_persona_switch_with_held_fetch_then_tap_recovers_the_new_personas_session() =>
        fixture.RunAsync(() => RunSwitchScenarioEAsync(holdFetch: true));
}

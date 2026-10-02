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
///  - Case C (<see cref="RunSwitchScenarioCAsync"/>): REWRITTEN in issue #180 round 3 (Rick's
///    round-2 review, R9) -- holds the <c>/api/personas/test-beta</c> detail fetch so the guest's
///    tap on "Start recording" lands squarely inside the pending-switch window, WHILE the OLD
///    socket is still genuinely OPEN (R7 made <c>endSession()</c> run strictly AFTER a successful
///    fetch, so the old "wait for the old socket to reach CLOSED first" precondition this case
///    used to have can no longer be constructed here at all -- see the method's own doc comment).
///    Now doubles as R8's required scenario: the deferred tap must not start a session on the OLD
///    socket while the switch is still pending, and must land on the NEW persona once it settles.
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
/// A seventh case (issue #180, <see cref="RunSwitchAfterReloadResumeScenarioAsync"/>) covers a
/// DIFFERENT dimension than A-E2 entirely: every one of those switches from a FRESH, order-less
/// test-alpha session, so none of them can prove the picker's own GH-180 bug -- the "Locked for
/// this order -- start a new order to switch" lock re-engaging specifically once a RELOAD
/// RESUMES a non-empty order. This case builds a real order (a scripted <c>update_order</c> call
/// attached to the server-side session), reloads, asserts the picker is still enabled and the
/// switch now opens <see cref="PersonaSwitchConfirmDialog"/> instead of switching immediately or
/// silently refusing, confirms the switch, taps, and then reuses the same five-property check
/// below plus one more: the old persona's order text is gone from the page entirely (no leak).
/// Issue #180 round 3 (R10) additionally inserts a Cancel round trip before the real switch,
/// asserting focus actually lands back on <c>#persona-picker</c> in a real browser.
///
/// All seven cases assert, against the SAME five properties Rick's review lists (case 7 adds the
/// no-leak check above, case C additionally asserts R8's own "nothing lands on the old socket
/// while the switch is still pending" property): the fake upstream connection for the test-beta
/// session receives the client's <c>session.update</c> (<c>turn_detection.type=="server_vad"</c>,
/// <c>threshold==0.7</c>); the greeting the server sends upstream for that connection contains
/// <see cref="PersonaSmokeExpectations.For"/>("test-beta").GreetingSubstring; the browser created
/// EXACTLY ONE socket for test-beta and sent <c>session.update</c> on it EXACTLY ONCE; no
/// test-alpha socket was created after the switch click; and the original test-alpha socket's
/// post-click frames exactly match whether it was still OPEN when the switch's own
/// <c>endSession()</c> ran (issue #180 round 3, R7/R9 -- see
/// <see cref="AssertSwitchDeliveredToNewPersonaAsync"/>'s own doc comment for why the previous
/// "all frames equal end_session" check could not tell "sent exactly one" apart from "sent none
/// at all", which Cases E/E2 actually exercise).
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
        await AssertSwitchDeliveredToNewPersonaAsync(
            page, socketsBeforeSwitch, alphaSentBeforeSwitch, secondConnection, expectEndSessionSent: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The five properties Rick's round-1 and round-2 reviews both list, shared by every case
    /// (A/B smoke, C/D deterministic detectors): the fake upstream connection for the
    /// switched-to persona received the client's <c>session.update</c> and that persona's
    /// greeting; the browser created EXACTLY ONE socket for the new persona and sent
    /// <c>session.update</c> on it EXACTLY ONCE; no socket for the OLD persona was created after
    /// the switch click; and the original OLD-persona socket's post-click frames exactly match
    /// <paramref name="expectEndSessionSent"/> (issue #180 round 3, Rick's round-2 review item
    /// R7/R9 -- the previous version of this last check, <c>alphaTypesAfter.All(...)</c>, was
    /// vacuously true on an EMPTY list, which is exactly what Cases E/E2 produce: their OLD
    /// socket is already closed by the backend's own idle sweep before the switch ever starts,
    /// so <c>endSession()</c> finds <c>openRef.current</c> already false and sends nothing on it
    /// at all -- "no frame other than end_session" was true, but so was "no end_session either",
    /// and the old assertion could not tell those apart. Pass <see langword="true"/> when the OLD
    /// socket was genuinely OPEN at the moment the switch's own <c>endSession()</c> ran (Cases
    /// A/B/C/D/7: exactly one <c>extension.end_session</c> frame and nothing else is required);
    /// pass <see langword="false"/> when it was already closed beforehand (Cases E/E2: nothing at
    /// all may land on it, since there is nothing left to end).
    /// </summary>
    private async Task AssertSwitchDeliveredToNewPersonaAsync(
        IPage page,
        int socketsBeforeSwitch,
        int alphaSentBeforeSwitch,
        FakeRealtimeConnection? secondConnection,
        bool expectEndSessionSent,
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

        // ── The original OLD (test-alpha) socket's post-click frames exactly match
        //    `expectEndSessionSent` (issue #180 round 3, R7/R9: see this method's own doc
        //    comment for why "all equal end_session" alone cannot distinguish "sent exactly one"
        //    from "sent none at all") ──
        var alphaSentAfter = await SentAsync(page, 0);
        var alphaTypesAfter = TypesOf(alphaSentAfter).Skip(alphaSentBeforeSwitch).ToList();
        if (expectEndSessionSent)
        {
            Assert.True(
                alphaTypesAfter.Count == 1 && alphaTypesAfter[0] == "extension.end_session",
                "Expected EXACTLY ONE extension.end_session frame and nothing else on the " +
                $"test-alpha socket after the switch click, got: [{string.Join(", ", alphaTypesAfter)}].");
        }
        else
        {
            Assert.True(
                alphaTypesAfter.Count == 0,
                "Expected NO frames at all on the test-alpha socket after the switch click (it " +
                "was already closed before the switch started, so endSession() had nothing to " +
                $"end), got: [{string.Join(", ", alphaTypesAfter)}].");
        }
    }

    [Fact]
    public Task Settled_tap_after_a_persona_switch_sends_session_update_and_gets_the_new_personas_greeting() =>
        fixture.RunAsync(() => RunSwitchScenarioAsync(clickImmediately: false));

    [Fact]
    public Task Immediate_tap_right_after_a_persona_switch_still_sends_session_update_and_gets_the_new_personas_greeting() =>
        fixture.RunAsync(() => RunSwitchScenarioAsync(clickImmediately: true));

    /// <summary>
    /// Case C (issue #171 round 3, originally Rick's round-2 review item 2) REWRITTEN and merged
    /// with R8 (issue #180 round 3, Rick's round-2 review item R8 -- REQUIRED). Originally this
    /// case held the <c>/api/personas/test-beta</c> detail fetch and then waited for the OLD
    /// socket's native <c>readyState</c> to reach CLOSED before tapping -- but issue #180 round
    /// 3, R7 made <c>endSession()</c> run strictly AFTER a successful persona fetch, so with the
    /// fetch held there is nothing left that could ever close the old socket during this window;
    /// the only thing that used to satisfy that wait was the backend's unrelated ~10s idle sweep
    /// racing the held fetch, which just duplicates Case E2 below (Rick's round-2 review, R9).
    /// Rewritten instead to tap while the OLD socket is still genuinely OPEN during the held
    /// fetch -- exactly R8's new required scenario (a mic tap while the new persona is still
    /// loading must not start the OLD persona's greeting) -- so the two are now one test:
    ///  - R8: captures test-alpha's sent-frame count immediately before the click, taps, waits,
    ///    and confirms the count is still UNCHANGED before the fetch is ever released -- the tap
    ///    must still be waiting for the switch to settle, not have started a session on the old
    ///    socket.
    ///  - R7 (the five properties, via <see cref="AssertSwitchDeliveredToNewPersonaAsync"/>):
    ///    once the fetch resolves, the deferred tap is honored on the NEW persona exactly once
    ///    (<c>realtime.startSession()</c>'s queued <c>session.update</c> flushes the moment the
    ///    test-beta socket opens -- <c>useRealtime.tsx</c>'s own <c>pendingRef</c>/<c>onOpen</c>
    ///    mechanism), and the OLD socket -- genuinely OPEN the entire time the switch's own
    ///    <c>endSession()</c> ran -- received EXACTLY ONE <c>extension.end_session</c> frame and
    ///    nothing else.
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

        // Hold the persona-detail fetch BEFORE the picker selection: the selection itself still
        // runs (beginSwitch()'s bookkeeping flips immediately), but endSession() -- issue #180
        // round 3, R7: only called once the fetch actually succeeds -- never fires, and the prop
        // change that would open the replacement socket never fires either, until `gate` is
        // released below.
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

        // Issue #180 round 3, R7: with the fetch held, endSession() has not run yet, so the OLD
        // socket must still be genuinely OPEN -- confirms this is the intended window (R8) before
        // tapping, not a race against the old wait-for-CLOSED step this case used to have.
        Assert.Equal(1 /* OPEN */, await ReadyStateAsync(page, 0).ConfigureAwait(false));

        var alphaSentBeforeTap = (await SentAsync(page, 0)).GetArrayLength();
        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);

        // Issue #180 round 3, R8: long enough for a regression (starting the OLD persona's
        // session on this tap) to have shown up, before the fetch is ever released -- nothing may
        // be sent on the OLD socket yet; the tap must still be waiting for the switch to settle.
        await Task.Delay(1000, ct).ConfigureAwait(false);
        var alphaSentStillPending = (await SentAsync(page, 0)).GetArrayLength();
        Assert.Equal(alphaSentBeforeTap, alphaSentStillPending);

        gate.SetResult();

        var secondConnection = await secondConnectionTask;
        await AssertSwitchDeliveredToNewPersonaAsync(
            page, socketsBeforeSwitch, alphaSentBeforeSwitch, secondConnection, expectEndSessionSent: true, ct).ConfigureAwait(false);
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
        await AssertSwitchDeliveredToNewPersonaAsync(
            page, socketsBeforeSwitch, alphaSentBeforeSwitch, secondConnection, expectEndSessionSent: true, ct).ConfigureAwait(false);
    }

    [Fact]
    public Task Mic_tap_while_the_switch_fetch_is_still_pending_waits_for_the_switch_then_starts_only_the_new_personas_session() =>
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
        await AssertSwitchDeliveredToNewPersonaAsync(
            page, socketsBeforeSwitch, alphaSentBeforeSwitch, secondConnection, expectEndSessionSent: false, ct).ConfigureAwait(false);
    }

    [Fact]
    public Task Idle_closed_socket_then_persona_switch_then_tap_recovers_the_new_personas_session() =>
        fixture.RunAsync(() => RunSwitchScenarioEAsync(holdFetch: false));

    [Fact]
    public Task Idle_closed_socket_then_persona_switch_with_held_fetch_then_tap_recovers_the_new_personas_session() =>
        fixture.RunAsync(() => RunSwitchScenarioEAsync(holdFetch: true));

    /// <summary>
    /// Issue #180 (the brief's own Browser-leg requirement): create an order, reload (resume),
    /// switch via the confirm dialog, tap -- then prove the server receives
    /// <c>session.update</c> for test-beta and the test-beta greeting on exactly one test-beta
    /// socket. Three things this case proves that A-E2 above cannot, since every one of them
    /// switches from a FRESH, order-less test-alpha session:
    ///  - The picker is never disabled, even once a reload has RESUMED a non-empty order -- the
    ///    exact state where, before this fix, the picker showed "Locked for this order -- start
    ///    a new order to switch" and no further &lt;select&gt; interaction could reach test-beta
    ///    at all (the issue's own repro).
    ///  - Selecting test-beta on that non-empty resumed order opens
    ///    <see cref="PersonaSwitchConfirmDialog"/> instead of switching immediately (cases A-E2's
    ///    empty-order path never shows this dialog) or silently refusing the switch (the old
    ///    lock).
    ///  - The OLD persona's order never leaks into the new one: test-alpha's order item (added
    ///    via a real scripted <c>update_order</c> call, attached to the SERVER-SIDE session so it
    ///    genuinely survives the reload like a guest's real order would -- not just pushed as
    ///    client-side UI state) is gone from the page entirely once the switch to test-beta
    ///    completes.
    ///  - Issue #180 round 1, R3 / round-3 review R10 (REQUIRED): before the real switch, Cancel
    ///    on the confirm dialog restores focus to <c>#persona-picker</c> -- previously only a
    ///    vitest assertion, never pinned against a real browser's actual focus state.
    /// Reuses <see cref="AssertSwitchDeliveredToNewPersonaAsync"/> for the same five properties
    /// every other case proves. <c>socketsBeforeSwitch</c>/<c>alphaSentBeforeSwitch</c> are
    /// captured AFTER the reload (not the original page load), since <c>window.__sockets</c>
    /// resets to a fresh array on every new document (<see cref="NewPageAsync"/>'s
    /// <c>context.AddInitScriptAsync</c> reruns on each one) -- the post-reload resumed
    /// connection is socket index 0 again, exactly like every other case's pre-switch test-alpha
    /// socket, so <see cref="AssertSwitchDeliveredToNewPersonaAsync"/>'s hard-coded "socket 0 is
    /// the old persona" check needs no change to be reused here.
    /// </summary>
    private async Task RunSwitchAfterReloadResumeScenarioAsync()
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

        // Build a REAL order against test-alpha: a scripted update_order call, which attaches
        // the item to the SERVER-SIDE session so it genuinely survives the reload below like a
        // guest's real order would, not just pushed as client-side UI state a hand-crafted frame
        // would be. Sourced from test-alpha's own smoke.json (PersonaSmokeTests.cs's own
        // no-persona-literal-in-shared-code convention) rather than a hardcoded item/size/price.
        var alphaItem = PersonaSmokeExpectations.For(PersonaSwitchBackendFixture.PersonaA);
        const string callId = "call_switch_after_reload_1";
        firstConnection!.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent(
                Name: "update_order",
                ArgumentsJson: JsonSerializer.Serialize(new
                {
                    action = "add",
                    item_name = alphaItem.OrderableItemName,
                    size = alphaItem.OrderableItemSize,
                    quantity = 1,
                    price = alphaItem.OrderableItemPrice,
                }),
                CallId: callId),
            new DoneEvent(),
        ]));

        // rtmt.py never handles a raw client response.create specially -- it passes straight
        // through to the upstream socket unchanged, exactly like a real VAD-triggered turn would.
        // useRealtime.tsx itself never sends response.create on its own (only the server does,
        // for the greeting/nudge -- see RealtimeBrowserClient.SendResponseCreateAsync's own doc
        // comment), so sending it directly on the live socket here (bypassing React/the mic
        // entirely, exactly like OrderScenarioHelpers.CallToolAsync does through the raw-socket
        // RealtimeBrowserClient harness) is the only black-box-safe way to reach a tool call
        // without simulating real audio/VAD timing -- see OrderResumeBrowserTests' own doc
        // comment for why that alternative was ruled out for a REAL Playwright page.
        await page.EvaluateAsync(
            "window.__sockets[0].ws.send(JSON.stringify({ type: 'response.create' }))").ConfigureAwait(false);

        await UntilAsync(
            () => ReceivedAsync(page, 0),
            received => TypesOf(received).Any(t => t == "extension.middle_tier_tool_response"),
            FrameTimeout, "extension.middle_tier_tool_response for update_order on the test-alpha socket", ct);

        // The REAL frontend actually rendered the item (not a simulated client-side push).
        await page.GetByText(alphaItem.OrderableItemName).First
            .WaitForAsync(new() { Timeout = (float)FrameTimeout.TotalMilliseconds }).ConfigureAwait(false);

        // Issue #180: before the fix, a non-empty order here would already show "Locked for this
        // order -- start a new order to switch" and disable the picker outright.
        Assert.True(await page.GetByLabel("Select persona").IsEnabledAsync().ConfigureAwait(false),
            "The persona picker must never be disabled (issue #180), even with a non-empty order.");

        // Reload: window.__sockets resets to a fresh array (the init script reruns on every new
        // document), so the post-reload connection is socket index 0 again.
        var reloadConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await page.ReloadAsync().ConfigureAwait(false);
        var reloadConnection = await reloadConnectionTask;
        Assert.True(reloadConnection is not null, $"No upstream connection was accepted on reload within {FrameTimeout}.");

        await UntilAsync(() => SocketCountAsync(page), n => n >= 1, FrameTimeout, "the post-reload realtime socket to open", ct);
        await UntilAsync(
            () => ReceivedAsync(page, 0),
            received => TypesOf(received).Any(t => t == "extension.session_resumed"),
            FrameTimeout, "extension.session_resumed on the post-reload socket", ct);

        // The resumed order survived the reload and rendered again -- and the picker is STILL
        // never disabled (issue #180's actual repro: the lock re-engaged specifically after a
        // reload resumed a non-empty order).
        await page.GetByText(alphaItem.OrderableItemName).First
            .WaitForAsync(new() { Timeout = (float)FrameTimeout.TotalMilliseconds }).ConfigureAwait(false);
        var picker = page.GetByLabel("Select persona");
        Assert.True(await picker.IsEnabledAsync().ConfigureAwait(false),
            "The persona picker must never be disabled after a reload resumes a non-empty order (issue #180).");

        var socketsBeforeSwitch = await SocketCountAsync(page);
        var alphaSentBeforeSwitch = (await SentAsync(page, 0)).GetArrayLength();

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);

        // The real PersonaPicker <select>, exactly as a guest would operate it -- never the URL
        // or a hook called directly.
        await picker.SelectOptionAsync(new SelectOptionValue { Value = PersonaSwitchBackendFixture.PersonaB }).ConfigureAwait(false);

        // Issue #180: a non-empty (here, resumed) order must open the confirm dialog instead of
        // switching immediately (cases A-E2's empty-order path) or silently refusing to switch at
        // all (the old lock).
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.WaitForAsync(new() { Timeout = (float)FrameTimeout.TotalMilliseconds }).ConfigureAwait(false);
        var dialogText = await dialog.InnerTextAsync().ConfigureAwait(false);
        // Generic, pack-driven copy (issue #180: no brand words in this shared-code dialog) --
        // "Test Beta Burger Co." is test-beta's own persona.json displayName, the only
        // persona-specific detail the dialog surfaces.
        Assert.Contains("Switching to Test Beta Burger Co. will start a new order.", dialogText, StringComparison.Ordinal);
        Assert.Contains("Your current order will be cleared.", dialogText, StringComparison.Ordinal);

        // Issue #180 round 1, R3 / round-3 review R10: Cancel must restore focus to the persona
        // picker (`PersonaSwitchConfirmDialog`'s own `onCloseAutoFocus`) -- already pinned by a
        // vitest unit test (`App.personaSwitch.test.tsx`), but never asserted in a REAL browser
        // until now. Cancel first, confirm focus actually landed back on `#persona-picker`
        // (Radix's own `onCloseAutoFocus` default does nothing useful here -- see that
        // component's doc comment), then re-select test-beta to reopen the dialog and continue
        // the rest of this scenario exactly as before.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync().ConfigureAwait(false);
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = (float)FrameTimeout.TotalMilliseconds })
            .ConfigureAwait(false);
        // Radix's unmount/focus-restore runs one tick after the dialog itself reports hidden --
        // WaitForFunctionAsync polls instead of taking a single immediate snapshot, which flaked
        // (observed empty activeElement.id) when asserted synchronously right after Hidden.
        await page.WaitForFunctionAsync(
                "() => document.activeElement && document.activeElement.id === 'persona-picker'",
                null,
                new() { Timeout = (float)FrameTimeout.TotalMilliseconds })
            .ConfigureAwait(false);
        var activeElementId = await page.EvaluateAsync<string?>("document.activeElement ? document.activeElement.id : null")
            .ConfigureAwait(false);
        Assert.Equal("persona-picker", activeElementId);

        await picker.SelectOptionAsync(new SelectOptionValue { Value = PersonaSwitchBackendFixture.PersonaB }).ConfigureAwait(false);
        await dialog.WaitForAsync(new() { Timeout = (float)FrameTimeout.TotalMilliseconds }).ConfigureAwait(false);

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Switch", Exact = true }).ClickAsync().ConfigureAwait(false);

        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync().ConfigureAwait(false);

        var secondConnection = await secondConnectionTask;
        await AssertSwitchDeliveredToNewPersonaAsync(
            page, socketsBeforeSwitch, alphaSentBeforeSwitch, secondConnection, expectEndSessionSent: true, ct).ConfigureAwait(false);

        // No leak: test-alpha's resumed order never carries over into the test-beta session.
        var bodyText = await page.Locator("body").InnerTextAsync().ConfigureAwait(false);
        Assert.DoesNotContain(alphaItem.OrderableItemName, bodyText, StringComparison.Ordinal);
    }

    [Fact]
    public Task Reload_then_resume_then_switch_through_the_confirm_dialog_delivers_only_to_the_new_personas_session() =>
        fixture.RunAsync(RunSwitchAfterReloadResumeScenarioAsync);
}

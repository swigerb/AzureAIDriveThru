using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.RateLimit;

/// <summary>
/// Issue #10: a pending rate-limit retry is cancelled the instant genuine guest speech reaches
/// the backend (app/backend/rate_limit.py's RateLimitRecovery.on_guest_speech(), invoked from
/// rtmt.py's from_server_to_client handling of the fake's own speech_started reply).
///
/// PR #123 review item 1 (issue #68): the previous version of
/// <see cref="A_pending_retry_is_cancelled_by_guest_speech"/> raced a fixed local guess against
/// this genuine network round trip (backend -> upstream -> speech_started reply ->
/// from_server_to_client resuming): it sent guest speech after an arbitrary fixed 200ms delay,
/// then bounded "no retried response.create" to a fixed 1.5s, all under the shared
/// <see cref="BackendProfiles.RateLimitTimers"/> profile's short 0.4s second-retry delay. Under
/// CPU load the round trip could occasionally lose that race to the retry's own timer -- a
/// genuine race, not a same-process ordering bug (rtmt.py's server-to-client loop is strictly
/// sequential). A rejected fix (reverted; see #68/#48 history) tried to close that race with a
/// cheaper, purely-local "audio forwarded upstream" signal instead of waiting for the model's own
/// VAD -- but the browser streams every mic buffer, including silence
/// (useAudioRecorder.tsx has no energy gate), so that signal would have cancelled every pending
/// retry, including the #48 greeting retry, the instant the mic reopened, leaving the carhop
/// silently unresponsive. `on_guest_speech()` (wired to the model's own VAD-confirmed
/// `speech_started`) remains the only guest-driven cancellation path. This class now:
/// (1) uses <see cref="BackendProfiles.RateLimitIdleInteractionTimers"/>, whose second-retry
/// delay is a generous 3.6s (see that profile's own doc comment) -- the round trip this test
/// waits on has no realistic way to lose that race under any load this suite exercises; and
/// (2) waits on the backend's own evidence -- the "Rate-limit retry cancelled: guest started
/// speaking" log line, via <see cref="IBackendUnderTest.WaitForDiagnosticsAsync"/> -- event-driven
/// rather than a fixed guess-and-hope delay. Gating on that log line costs no wall time:
/// `RateLimitRecovery._cancel_pending` calls the pending `asyncio.Task`'s `cancel()` and logs
/// synchronously, with no `await` in between and (single-threaded asyncio) nothing else able to
/// run in that gap, so observing the log line is itself proof the retry can never fire
/// afterward.
///
/// This scenario needs its own dedicated collection instead of sharing
/// <see cref="RateLimitRecoveryTests"/>'s ShortTimers collection, for a reason that has nothing
/// to do with rate-limit timing at all: app/backend/audio_pipeline.py's EchoSuppressor. Every
/// fresh (non-resumed) connection's greeting is answered by RealtimeBrowserClient's AutoRespond
/// default script with a real audio delta, so echo.on_audio_done() always fires and starts a
/// `greeting_in_progress`-doubled cooldown (config.yaml's audio.echo_cooldown_seconds is 1.5,
/// doubled to 3.0s post-greeting) during which rtmt.py's from_client_to_server loop silently
/// `continue`s past *every* input_audio_buffer.append -- it never reaches the fake upstream, so
/// the fake's WithVadDefaults() rule never replies with speech_started, so on_guest_speech() is
/// never invoked at all. This is a genuine, deliberate anti-echo production safety feature (real
/// callers' own mic audio picking up the assistant's TTS would otherwise falsely look like guest
/// speech), not a test bug or a rate-limit-specific race -- but it means this scenario's synthetic
/// guest-speech append must be sent *after* that fixed 3.0s cooldown has elapsed. There is no
/// CONFORMANCE_* hook for echo_cooldown_seconds itself (unlike the timers this profile does
/// override), so the wait below is real wall-clock time, not a shortened one.
/// </summary>
[Collection(RateLimitIdleInteractionTimersConformanceCollection.Name)]
public sealed class RateLimitGuestSpeechCancellationTests(RateLimitIdleInteractionTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Comfortably longer than the fixed 3.0s post-greeting echo-suppression cooldown
    /// (audio.echo_cooldown_seconds=1.5, doubled because the greeting itself is in progress) --
    /// see this class's doc comment. Sending guest speech any earlier is silently dropped by
    /// rtmt.py's echo-suppression `continue` before it ever reaches the fake upstream.</summary>
    private static readonly TimeSpan EchoCooldownWait = TimeSpan.FromSeconds(3.3);

    /// <summary>Enqueues a rate-limited failure with no parseable hint, so retry_delay() falls
    /// through to the profile-overridden default instead of clamping a hint.</summary>
    private static ResponseScript NoHintRateLimited() =>
        new([new DoneEvent(Status: "failed", ErrorCode: "rate_limit_exceeded", ErrorMessage: null)]);

    /// <summary>
    /// Takes an already-started <paramref name="connectionTask"/> rather than registering its own
    /// wait, so callers must call <c>fixture.Realtime.WaitForNextConnectionAsync(...)</c> *before*
    /// <c>RealtimeBrowserClient.ConnectAsync</c> -- matching the established, race-free ordering
    /// used everywhere else in this suite (e.g. SmokeSessionBootstrapTests). Registering the wait
    /// only after the browser is already connected is a genuine TOCTOU race:
    /// WaitForNextConnectionAsync deliberately only resolves a connection accepted *after* it's
    /// called, so if the backend's own eager upstream-connect (triggered by the browser's
    /// handshake) completes before this registers, it's missed entirely and this hangs for the
    /// full FrameTimeout -- confirmed to reproduce under a full-suite run's heavier concurrent load.
    /// </summary>
    private static async Task<FakeRealtimeConnection> ConnectAndGetPastGreetingAsync(
        Task<FakeRealtimeConnection?> connectionTask, RealtimeBrowserClient browser, CancellationToken ct)
    {
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Same marker UpdateOrderToolCallTests uses: the greeting's round trip must fully finish
        // (tools_pending cleared, ladder idle) before scripting the scenario's own turn, or the
        // automatic greeting response.create would consume our script instead. It also means the
        // greeting's own audio.done has already fired by the time this returns, so the echo
        // cooldown wait below starts from a stable, already-elapsed baseline.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");
        return connection!;
    }

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_pending_retry_is_cancelled_by_guest_speech() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await ConnectAndGetPastGreetingAsync(connectionTask, browser, ct);

        // Wait out the greeting's own echo-suppression cooldown (see class doc comment) *before*
        // starting the rate-limit ladder at all, so the guest-speech append below lands
        // comfortably past the cooldown window rather than racing it.
        await Task.Delay(EchoCooldownWait, ct);

        // Taken before the ladder starts, so the WaitForDiagnosticsAsync call below only ever
        // matches a cancellation log line produced by *this* scenario, not (for example) an
        // identically-worded line an earlier scenario sharing this collection's backend process
        // happened to log first (#66 S2).
        var diagnosticsWatermark = fixture.Backend!.DiagnosticsWatermark;

        // Two scripted failures: attempt 0 (silent) and retry 1 (notifies {attempt:1}) -- the
        // ladder's own second retry (attempt 2) is by then scheduled ("pending"). That
        // notification is the first client-visible, race-free signal that a retry is genuinely
        // pending: attempt 0's own failure is silent by design (no client-visible signal at
        // all), and FakeRealtimeUpstreamServer dispatches each received frame as an independent
        // fire-and-forget task (see FakeRealtimeUpstreamServer.HandleFrameAsync's caller,
        // `TrackHandler(...)`, not an inline await) -- racing guest speech against attempt 0's
        // own still-in-flight response would only prove which of two concurrent fake-side tasks
        // happened to finish first, not anything about RateLimitRecovery's own cancellation.
        connection.Script.Enqueue(NoHintRateLimited());
        connection.Script.Enqueue(NoHintRateLimited());
        await browser.SendResponseCreateAsync(ct);

        var attempt1Notification = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited" && f.Json.GetProperty("attempt").GetInt32() == 1,
            FrameTimeout, ct);
        Assert.True(attempt1Notification is not null,
            "Expected extension.rate_limited{attempt:1} after the first retry also failed.");

        // The boundary for "no further retry reaches the fake": retry 1's own response.create,
        // the last one the fake has seen so far. Attempt 2 is pending now (its own second-retry
        // delay is 3.6s on this profile -- see class doc comment) -- guest speech must cancel it
        // before it ever reaches the fake.
        var boundary = connection.ReceivedFrames.Snapshot().Last(f => f.Type == "response.create").Sequence;

        // input_audio_buffer.append is forwarded upstream (echo suppression's cooldown has long
        // since elapsed by now), where WithVadDefaults' rule replies with speech_started --
        // rtmt.py's handling of that reply is what actually calls recovery.on_guest_speech() (see
        // rtmt.py: "elif _MARKER_SPEECH_STARTED in data: ... recovery.on_guest_speech()").
        await browser.SendInputAudioAppendAsync("dGVzdA==", ct);

        // Event-driven, not a fixed-delay guess: wait for the backend's own log evidence that
        // on_guest_speech() actually ran and found (and cancelled) a pending retry. See class doc
        // comment for why observing this line is itself proof the retry can never fire
        // afterward, no margin needed.
        var cancelled = await fixture.Backend!.WaitForDiagnosticsAsync(
            d => d.Contains("Rate-limit retry cancelled: guest started speaking", StringComparison.Ordinal),
            FrameTimeout, ct, sinceWatermark: diagnosticsWatermark);
        Assert.True(cancelled,
            "Expected the backend to log \"Rate-limit retry cancelled: guest started speaking\" after " +
            "guest speech reached it.\n\n--- backend stdout/stderr ---\n" + fixture.Backend!.DumpDiagnostics());

        // A short, generous sanity bound (not the sole guarantee -- the log line above already is
        // one) that nothing slipped through regardless.
        var thirdResponseCreate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > boundary && f.Type == "response.create", TimeSpan.FromSeconds(1), ct);
        Assert.True(thirdResponseCreate is null,
            "Expected no retried response.create -- guest speech should have cancelled the pending retry.");

        var furtherNotification = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited" && f.Sequence > attempt1Notification!.Sequence,
            TimeSpan.FromSeconds(1), ct);
        Assert.True(furtherNotification is null, "A cancelled retry must never notify the browser.");
    });

    /// <summary>
    /// Regression row for PR #123 review item 1 (issue #68): while a retry is pending, the
    /// browser keeps streaming every mic buffer upstream, including silence
    /// (useAudioRecorder.tsx has no energy gate) -- but if the fake never reports
    /// `speech_started` for any of them (scripted off below, standing in for a genuinely quiet
    /// buffer the model's own real VAD wouldn't trigger on either), `on_guest_speech()` is never
    /// invoked. The pending retry must still fire on schedule: `rate_limit.py` has exactly one
    /// guest-driven cancellation path (`on_guest_speech()`, wired only to the model's own
    /// VAD-confirmed acknowledgment) and nothing cheaper is allowed to substitute for it. This
    /// pins the contract Rick's review called out so a cheaper "earlier signal" (the rejected
    /// `on_guest_audio_forwarded()` -- see this class's doc comment) can't come back: forwarded
    /// audio alone, without a confirmed `speech_started`, must never cancel a pending retry.
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_pending_retry_still_fires_while_the_browser_keeps_streaming_audio_without_speech_started() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await ConnectAndGetPastGreetingAsync(connectionTask, browser, ct);

        await Task.Delay(EchoCooldownWait, ct);

        // Opt out of WithVadDefaults' speech simulation for this row only: appends must still
        // reach the fake (proving echo suppression isn't what's blocking them), but never get a
        // synthetic speech_started reply -- isolating exactly the "no VAD confirmation" case
        // the rejected forwarded-audio signal would have mishandled.
        connection.Script.RemoveVadSpeechDefaultRule();

        connection.Script.Enqueue(NoHintRateLimited());
        connection.Script.Enqueue(NoHintRateLimited());
        await browser.SendResponseCreateAsync(ct);

        var attempt1Notification = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.rate_limited" && f.Json.GetProperty("attempt").GetInt32() == 1,
            FrameTimeout, ct);
        Assert.True(attempt1Notification is not null,
            "Expected extension.rate_limited{attempt:1} after the first retry also failed.");

        var boundary = connection.ReceivedFrames.Snapshot().Last(f => f.Type == "response.create").Sequence;

        // The browser keeps streaming mic audio the whole time the retry is pending, same as a
        // real guest's open mic -- none of it gets a speech_started reply (the rule was removed
        // above), so on_guest_speech() never runs for any of these frames.
        for (var i = 0; i < 6; i++)
        {
            await browser.SendInputAudioAppendAsync("dGVzdA==", ct);
            await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
        }

        // PR #123 review item 1: this row's own precondition. The appends above "must still
        // reach the fake (proving echo suppression isn't what's blocking them)" per this
        // method's doc comment, but nothing checked that until now -- if the echo cooldown ever
        // grew past EchoCooldownWait, every append would be silently dropped by rtmt.py's
        // echo-suppression `continue` before reaching the fake, and this row would still pass
        // vacuously (the retry fires on its own timer either way). Asserting at least one append
        // past `boundary` actually landed at the fake closes that gap, so an echo-cooldown
        // regression can no longer make this row pass for the wrong reason.
        Assert.Contains(connection.ReceivedFrames.Snapshot(),
            f => f.Sequence > boundary && f.Type == "input_audio_buffer.append");

        // Keep streaming mic audio past the initial burst above, all the way until the retry
        // itself arrives -- matching this row's own doc comment ("the browser keeps streaming
        // every mic buffer upstream ... while a retry is pending"), rather than stopping after a
        // fixed handful of frames sent well before the profile's 3.6s second-retry delay
        // elapses. None of these get a speech_started reply either (the rule stays removed for
        // the whole row), so they can't accidentally cancel the retry themselves.
        using var streamingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamingTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await browser.SendInputAudioAppendAsync("dGVzdA==", streamingCts.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(150), streamingCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: cancelled below once the retry's own response.create is observed.
            }
        }, ct);

        // CONFORMANCE_RATE_LIMIT_SECOND_RETRY_DELAY_SECONDS=3.6 on this profile (see class doc
        // comment) -- the pending retry must still reach the fake on schedule despite the
        // ongoing, speech_started-free audio stream above.
        var retriedResponseCreate = await connection.ReceivedFrames.WaitForAsync(
            f => f.Sequence > boundary && f.Type == "response.create", FrameTimeout, ct);
        Assert.True(retriedResponseCreate is not null,
            "Expected the pending retry's response.create to still fire even though the browser kept " +
            "sending audio frames the fake never acknowledged with speech_started.");

        streamingCts.Cancel();
        await streamingTask;
    });
}

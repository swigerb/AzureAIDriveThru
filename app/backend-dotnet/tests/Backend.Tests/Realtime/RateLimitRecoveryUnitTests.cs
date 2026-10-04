using System.Reflection;
using System.Text.Json.Nodes;
using Backend.Realtime;
using Microsoft.Extensions.Time.Testing;

namespace Backend.Tests.Realtime;

/// <summary>
/// Issue #13 Wave 4: byte-for-byte-behaviour port tests for app/backend/rate_limit.py's
/// <c>RateLimitRecovery</c>, exercised directly against <see cref="RateLimitRecovery"/> (no
/// <see cref="Backend.Sessions.RealtimeProcessor"/> / live sockets needed -- same isolation
/// technique as <c>RateLimitDetectionTests</c>/<c>AudioAppendFastPathTests</c>). A dedicated,
/// deliberately differently-named class from the conformance suite's own
/// <c>Conformance.Tests.Scenarios.RateLimit.RateLimitRecoveryTests</c> (different project,
/// different namespace, but same class basename would still be confusing side by side).
///
/// Every test uses a <see cref="FakeTimeProvider"/> (Issue #13 Wave 2's deterministic-timer
/// idiom, reused verbatim from <see cref="EchoSuppressor"/>'s own delayed-flush tests): advancing
/// past a scheduled retry's delay runs its <c>Task.Delay(..., _timeProvider, ...).ContinueWith(...,
/// TaskContinuationOptions.ExecuteSynchronously, ...)</c> continuation inline, on the same thread,
/// inside the <c>Advance</c> call itself -- no sleeps, no polling, no flakiness.
/// </summary>
public sealed class RateLimitRecoveryUnitTests
{
    private static JsonObject RateLimitError(string? message = null) =>
        new() { ["code"] = "rate_limit_exceeded", ["message"] = message };

    private static JsonObject ErrorEvent(JsonObject error) => new() { ["type"] = "error", ["error"] = error };

    private static JsonObject ResponseDoneFailed(JsonObject error, string? responseId = "resp_1") =>
        new()
        {
            ["type"] = "response.done",
            ["response"] = new JsonObject
            {
                ["id"] = responseId,
                ["status"] = "failed",
                ["status_details"] = new JsonObject { ["error"] = error },
            },
        };

    private static JsonObject ResponseDoneOk(string? responseId = "resp_1") =>
        new()
        {
            ["type"] = "response.done",
            ["response"] = new JsonObject { ["id"] = responseId, ["status"] = "completed" },
        };

    private sealed class Harness
    {
        public RateLimitRecovery Recovery { get; }
        public FakeTimeProvider Time { get; } = new();
        public List<string> Upstream { get; } = [];
        public List<JsonObject> Client { get; } = [];

        public Harness(RateLimitSettings? settings = null)
        {
            Recovery = new RateLimitRecovery(
                settings ?? new RateLimitSettings(),
                sendUpstream: (payload, _) => { Upstream.Add(payload); return Task.CompletedTask; },
                sendClient: (payload, _) => { Client.Add(payload); return Task.CompletedTask; },
                timeProvider: Time,
                sessionId: "s1");
        }
    }

    // ── enabled gate ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_IgnoresFailures_NoRetryNoNotify()
    {
        var h = new Harness(new RateLimitSettings(enabled: false));

        var handledError = await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        var handledDone = await h.Recovery.OnResponseDoneAsync(ResponseDoneFailed(RateLimitError()), CancellationToken.None);

        Assert.False(handledError);
        Assert.False(handledDone);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Upstream);
        Assert.Empty(h.Client);
    }

    // ── happy-path ladder, via the `error` event hook ───────────────────────────────────────

    [Fact]
    public async Task FirstFailure_SilentlySchedulesRetry_NoNotifyYet()
    {
        var h = new Harness();

        var handled = await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);

        Assert.True(handled);
        Assert.Empty(h.Client); // silent -- the first retry never tells the browser.
        Assert.Empty(h.Upstream); // not yet -- still waiting out the 1.5s default delay.
        Assert.True(h.Recovery.Busy);

        h.Time.Advance(TimeSpan.FromSeconds(1.5));

        Assert.Single(h.Upstream);
        Assert.Equal("""{"type":"response.create"}""", h.Upstream[0]);
        Assert.Equal(1, h.Recovery.RetriesSent);
        Assert.Empty(h.Client);
    }

    [Fact]
    public async Task FullLadderToExhaustion_NotifiesNonFinalThenFinal_AndStops()
    {
        var h = new Harness();

        // Attempt 1: silent retry.
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Single(h.Upstream);
        Assert.Empty(h.Client);

        // Our retry's own response.created arrives, then it also fails -- reported the realistic
        // way (via that response's own response.done(status=failed), not a standalone `error`
        // event -- while a response is in flight, a standalone `error` is deferred to response.done
        // instead, to avoid double-counting the same failure). Should notify attempt 1 (non-final)
        // and schedule the second retry.
        h.Recovery.OnResponseCreated();
        await h.Recovery.OnResponseDoneAsync(ResponseDoneFailed(RateLimitError()), CancellationToken.None);

        Assert.Single(h.Client);
        Assert.Equal("extension.rate_limited", h.Client[0]["type"]!.GetValue<string>());
        Assert.Equal(1, h.Client[0]["attempt"]!.GetValue<int>());
        Assert.False(h.Client[0].TryGetPropertyValue("final", out _)); // attempt:1 must NOT carry `final`.
        Assert.Single(h.Upstream); // retry 2 not sent yet -- still waiting out the 4.0s delay.

        h.Time.Advance(TimeSpan.FromSeconds(4.0));
        Assert.Equal(2, h.Upstream.Count);
        Assert.Equal(2, h.Recovery.RetriesSent);

        // Retry 2 also fails -- exhausted: final notice, no third retry ever scheduled.
        h.Recovery.OnResponseCreated();
        await h.Recovery.OnResponseDoneAsync(ResponseDoneFailed(RateLimitError()), CancellationToken.None);

        Assert.Equal(2, h.Client.Count);
        Assert.Equal(2, h.Client[1]["attempt"]!.GetValue<int>());
        Assert.True(h.Client[1]["final"]!.GetValue<bool>());
        Assert.False(h.Recovery.Busy);

        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(2, h.Upstream.Count); // still exactly 2 -- no third retry.

        // A further failure report after exhaustion (e.g. a stray late error) is ignored outright.
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        Assert.Equal(2, h.Client.Count);
        Assert.Equal(2, h.Upstream.Count);

        // The guest's next turn starts the ladder over from scratch.
        h.Recovery.OnGuestSpeech();
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        Assert.Equal(2, h.Client.Count); // still silent -- fresh attempt 1 again.
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(3, h.Upstream.Count);
    }

    // ── response.done closes the real gap: today's C# never even looked at response.done ────

    [Fact]
    public async Task OnResponseDoneAsync_DetectsRateLimitFailure_ClosingTheProductionGap()
    {
        var h = new Harness();

        var handled = await h.Recovery.OnResponseDoneAsync(ResponseDoneFailed(RateLimitError()), CancellationToken.None);

        Assert.True(handled);
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Single(h.Upstream);
    }

    [Fact]
    public async Task OnResponseDoneAsync_NonRateLimitFailure_NotHandled()
    {
        var h = new Harness();
        var handled = await h.Recovery.OnResponseDoneAsync(
            ResponseDoneFailed(new JsonObject { ["code"] = "content_filter" }), CancellationToken.None);

        Assert.False(handled);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Upstream);
    }

    [Fact]
    public async Task OnResponseDoneAsync_Success_ResetsAttemptCounter()
    {
        var h = new Harness();

        // Fail once (attempt 1 scheduled), but before it fires, a genuinely successful response
        // elsewhere -- OnGuestSpeech isn't the only reset path; a clean response.done is too once
        // it's not a retry's own failure.
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        h.Recovery.OnResponseCreated();
        var handled = await h.Recovery.OnResponseDoneAsync(ResponseDoneOk(), CancellationToken.None);

        Assert.False(handled); // not a rate-limit failure -- caller forwards response.done as-is.
        Assert.False(h.Recovery.Busy);

        // Next failure starts a fresh ladder (attempt 1 again, still silent).
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        Assert.Empty(h.Client);
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, h.Upstream.Count);
    }

    // ── error event vs. response.done interplay: error while in flight waits for response.done ─

    [Fact]
    public async Task OnErrorAsync_WhileResponseInFlight_WaitsForResponseDone_NoImmediateRetry()
    {
        var h = new Harness();
        h.Recovery.OnResponseCreated(); // a normal (non-retry) response starts.

        var handled = await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);

        Assert.True(handled); // swallowed -- but...
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Upstream); // ...no retry scheduled from the bare error event alone.

        // The response's own response.done reports the SAME failure -- now it retries.
        await h.Recovery.OnResponseDoneAsync(ResponseDoneFailed(RateLimitError()), CancellationToken.None);
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Single(h.Upstream);
    }

    // ── cancellation sources ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnGuestSpeech_CancelsPendingRetry()
    {
        var h = new Harness();
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        Assert.True(h.Recovery.Busy);

        h.Recovery.OnGuestSpeech();

        Assert.False(h.Recovery.Busy);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Upstream); // the scheduled retry never fires.
    }

    [Fact]
    public async Task OnExternalResponseCreate_CancelsPendingRetry()
    {
        var h = new Harness();
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);

        h.Recovery.OnExternalResponseCreate("tool follow-up");

        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Upstream);
    }

    [Fact]
    public async Task OnResponseCreated_ForSomeoneElsesResponse_CancelsPendingRetry()
    {
        var h = new Harness();
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);

        // e.g. VAD auto-responded, or the greeting fired, before our retry's delay elapsed.
        h.Recovery.OnResponseCreated();

        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Upstream);
    }

    [Fact]
    public async Task OnResponseCreated_ForOurOwnRetry_DoesNotCancelItself()
    {
        var h = new Harness();
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        h.Time.Advance(TimeSpan.FromSeconds(1.5)); // retry fires -- _awaitingRetry is now true.

        // The upstream's own response.created event for OUR retry must not cancel anything (there
        // is nothing pending to cancel) or reset the attempt counter.
        h.Recovery.OnResponseCreated();

        Assert.True(h.Recovery.Busy); // still "busy" -- the retry's response hasn't finished yet.
        Assert.Single(h.Upstream); // unchanged.
    }

    [Fact]
    public void Cancel_DropsPendingRetry()
    {
        var h = new Harness();
        _ = h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);

        h.Recovery.Cancel("socket closed");

        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(h.Upstream);
        Assert.False(h.Recovery.Busy);
    }

    // ── tool-follow-up interplay: RealtimeProcessor's own auto response.create after tool
    // results never goes through OnExternalResponseCreate (see RealtimeProcessor.cs's
    // HandleResponseDoneAsync) -- so it must not disturb an unrelated pending retry either. ────

    [Fact]
    public async Task UnrelatedToolFollowUp_SeparateFromLadder_DoesNotInterfere()
    {
        var h = new Harness();
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);

        // Nothing from a tool-call response.done path calls any RateLimitRecovery hook at all
        // (RealtimeProcessor sends that response.create directly) -- confirm our own pending retry
        // survives untouched.
        Assert.True(h.Recovery.Busy);
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Single(h.Upstream);
    }

    // ── retry-hint parsing and clamping (ParseRetryHint / RetryDelay, internal statics) ──────

    [Theory]
    [InlineData("Please try again in 3 seconds.", 3.0)]
    [InlineData("please retry after 250ms", 0.25)]
    [InlineData("Try again in 1.5s", 1.5)]
    [InlineData("retry in 7 secs", 7.0)]
    [InlineData("nothing useful here", null)]
    [InlineData(null, null)]
    public void ParseRetryHint_MatchesPythonsRegexContract(string? text, double? expected)
    {
        var hint = RateLimitRecovery.ParseRetryHint(text);
        if (expected is null)
        {
            Assert.Null(hint);
        }
        else
        {
            Assert.NotNull(hint);
            Assert.Equal(expected.Value, hint!.Value, precision: 6);
        }
    }

    [Theory]
    [InlineData(null, 1.5, 0.5, 5.0, 1.5)] // no hint -> default.
    [InlineData(0.1, 1.5, 0.5, 5.0, 0.5)] // below the first-retry floor -> clamped up.
    [InlineData(9.0, 1.5, 0.5, 5.0, 5.0)] // above the first-retry ceiling -> clamped down.
    [InlineData(3.0, 1.5, 0.5, 5.0, 3.0)] // within bounds -> used as-is.
    [InlineData(1.0, 4.0, 2.0, 8.0, 2.0)] // below the second-retry floor -> clamped up.
    [InlineData(20.0, 4.0, 2.0, 8.0, 8.0)] // above the second-retry ceiling -> clamped down.
    public void RetryDelay_ClampsHintIntoBounds(double? hint, double defaultSeconds, double low, double high, double expected)
    {
        Assert.Equal(expected, RateLimitRecovery.RetryDelay(hint, defaultSeconds, (low, high)));
    }

    [Fact]
    public async Task FirstRetry_UsesHintClampedToFirstRetryBounds()
    {
        var h = new Harness();
        // The service's own hint (9s) exceeds the first-retry ceiling (5.0s) -- must clamp down.
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError("Please try again in 9 seconds.")), CancellationToken.None);

        h.Time.Advance(TimeSpan.FromSeconds(4.99));
        Assert.Empty(h.Upstream); // not yet -- clamped to 5.0s, not the raw 9s hint nor the 1.5s default.

        h.Time.Advance(TimeSpan.FromSeconds(0.02));
        Assert.Single(h.Upstream);
    }

    // ── white-box: RunRetryAsync's defensive response-in-flight skip ────────────────────────
    //
    // Unreachable through any public hook alone: every hook that can observe a genuinely in-flight
    // response (OnResponseCreated/OnExternalResponseCreate/OnGuestSpeech) also cancels any pending
    // retry in the same call, by design (see this class's own doc comment on why scheduling is
    // locked together with the decision to schedule). The ONLY way `RunRetryAsync` can still see
    // `_responseInFlight == true` is a genuine two-loop race: a cancellation signal lands a moment
    // AFTER the delay's timer has already elapsed (Task.Delay cannot retroactively become
    // Canceled once it has already completed) -- impractical to reproduce deterministically with
    // real threads/timing. This test drives that exact internal state directly via reflection on
    // the private `_responseInFlight` field (same class, so this is a white-box test of
    // RateLimitRecovery's own private invariant, not a test of any public contract), then lets the
    // real, already-scheduled continuation fire via the normal Advance() mechanism -- proving the
    // skip-and-reset actually happens instead of sending a second, overlapping response.create.
    [Fact]
    public async Task RunRetryAsync_SkipsSend_WhenResponseAlreadyInFlight()
    {
        var h = new Harness();
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        Assert.True(h.Recovery.Busy);

        // Simulate the race itself: flip the private flag directly (bypassing the public
        // OnResponseCreated(), which would otherwise cancel this very pending retry as part of its
        // own normal, non-racy behaviour) so the retry already scheduled above is the one and only
        // delayed continuation in flight when time advances past its due time.
        var recoveryType = typeof(RateLimitRecovery);
        var inFlightField = recoveryType.GetField("_responseInFlight", BindingFlags.NonPublic | BindingFlags.Instance)!;
        inFlightField.SetValue(h.Recovery, true);

        h.Time.Advance(TimeSpan.FromSeconds(1.5)); // runs the real scheduled continuation inline.

        Assert.Empty(h.Upstream); // the retry must NOT fire a second response.create.
        Assert.False(h.Recovery.Busy); // and must cleanly reset instead of getting stuck "awaiting".

        // The injected `_responseInFlight=true` was purely to force the skip branch above, not a
        // real in-flight response (nothing here ever called OnResponseCreated) -- clear it back to
        // reflect reality before driving the next failure through the normal `error`-event path.
        inFlightField.SetValue(h.Recovery, false);

        // The next failure starts a fresh ladder (attempt was reset to 0, not left at 1).
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Single(h.Upstream);
    }

    // ── Rick's #235 review: a stale timer continuation racing a cancellation source ─────────
    //
    // The bug: Task.Delay(...).ContinueWith(t => { if (!t.IsCanceled) RunRetryAsync(...); })
    // checks t.IsCanceled OUTSIDE _sync, and a Task.Delay cannot retroactively become Canceled
    // once it has already elapsed. So if a cancellation source (guest speech, the browser's own
    // response.create, teardown) runs AFTER the timer has fired but BEFORE the continuation
    // acquires _sync, the continuation still runs -- sending a stale/duplicate response.create
    // (possibly onto an already-closing socket) and/or corrupting _attempt/_awaitingRetry for a
    // retry nobody asked for any more. Reproduced here by holding _sync on this thread (via the
    // SyncRootForTests test hook) while the fake clock is advanced on a background thread: the
    // background continuation blocks on _sync until this thread's cancellation call (reentrant
    // on the same monitor) has already fully committed, deterministically recreating the race
    // without any flaky real-time sleeps on the assertion path itself.
    private static async Task RunStaleRetryRaceAsync(Harness h, Action<RateLimitRecovery> duringTheRace)
    {
        var syncRoot = h.Recovery.SyncRootForTests;
        Task advanceTask;
        Monitor.Enter(syncRoot);
        try
        {
            advanceTask = Task.Run(() => h.Time.Advance(TimeSpan.FromSeconds(1.5)));
            // Bias scheduling so the background thread's stale continuation actually reaches
            // (and blocks on) _sync before this thread wins the race -- the Monitor itself (not
            // this sleep) is what makes the final outcome deterministic. A too-short sleep only
            // risks a false pass (nothing to race against yet), never a false failure.
            Thread.Sleep(100);
            duringTheRace(h.Recovery);
        }
        finally
        {
            Monitor.Exit(syncRoot);
        }

        var finished = await Task.WhenAny(advanceTask, Task.Delay(TimeSpan.FromSeconds(5))) == advanceTask;
        Assert.True(finished, "the stale continuation deadlocked instead of returning once superseded");
        await advanceTask; // surface any exception thrown on the background thread.
    }

    public static IEnumerable<object[]> CancellationSources()
    {
        yield return new object[] { "guest speech", (Action<RateLimitRecovery>)(r => r.OnGuestSpeech()) };
        yield return new object[]
        {
            "browser response.create", (Action<RateLimitRecovery>)(r => r.OnExternalResponseCreate("browser")),
        };
        yield return new object[] { "teardown", (Action<RateLimitRecovery>)(r => r.Cancel("socket closed")) };
    }

    [Theory]
    [MemberData(nameof(CancellationSources))]
    public async Task StaleTimerContinuation_LosesRaceToCancellation_SendsNothingAndStaysClean(
        string _, Action<RateLimitRecovery> cancellationSource)
    {
        var h = new Harness();
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        Assert.True(h.Recovery.Busy);

        await RunStaleRetryRaceAsync(h, cancellationSource);

        Assert.Empty(h.Upstream); // the stale retry must never reach the upstream send.
        Assert.False(h.Recovery.Busy); // clean idle state, not stuck mid-retry bookkeeping.

        // And the ladder genuinely restarted clean: the next failure is attempt 1 again (silent),
        // not corrupted leftover state from the superseded retry (which would have forced
        // _attempt=1/_awaitingRetry=true regardless of what the cancellation source itself did).
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        Assert.Empty(h.Client); // still silent -- a fresh attempt 1, not a corrupted later attempt.
        h.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Single(h.Upstream);
    }

    [Fact]
    public async Task StaleTimerContinuation_LosesRaceToANewlyScheduledRetry_OnlyTheNewOneSends()
    {
        var h = new Harness();
        await h.Recovery.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None);
        Assert.True(h.Recovery.Busy);

        // The race window: guest speech cancels the stale retry AND a brand-new rate-limit
        // failure immediately schedules a fresh one of its own -- both committed, under the
        // same lock this thread is holding, before the stale continuation above can ever reach
        // it.
        await RunStaleRetryRaceAsync(h, r =>
        {
            r.OnGuestSpeech();
            var handled = r.OnErrorAsync(ErrorEvent(RateLimitError()), CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.True(handled);
        });

        Assert.Empty(h.Upstream); // the new retry hasn't fired yet -- still waiting its own fresh delay.
        Assert.True(h.Recovery.Busy); // ...but it IS pending (unlike the stale one it replaced).

        h.Time.Advance(TimeSpan.FromSeconds(1.5)); // the NEW retry's own delay elapses.

        Assert.Single(h.Upstream); // exactly one send -- from the new retry; the stale one sent nothing.
    }
}

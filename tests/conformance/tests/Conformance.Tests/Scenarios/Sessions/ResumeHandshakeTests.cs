using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Sessions;

/// <summary>
/// Issue #10: the `extension.resume` handshake itself. app/backend/rtmt.py only ever inspects
/// the *very first* client frame for `extension.resume` (first_frame_pending); once any frame —
/// resume or not — has been seen, the resume decision is locked in for the life of the socket.
/// A non-resume first frame (or the first_frame_timeout_seconds deadline, covered by
/// IdleTimeoutTests) decides "fresh" immediately and announces `extension.session_metadata`
/// with a fresh resumeId. A resume attempt is validated by session_manager.py's `resume()`:
/// malformed (wrong length), unknown (right length, never issued or already consumed), and
/// expired (covered by IdleTimeoutTests' boundary test) are all rejected with
/// `extension.resume_rejected {reason}` followed by a fresh re-announce; a *valid* resume is
/// single-use (the presented id is popped before a rotated one is issued) and, if the resumed
/// session is still attached to another socket, that socket is superseded with 4002.
/// </summary>
[Collection(ResumeTimersConformanceCollection.Name)]
public sealed class ResumeHandshakeTests(ResumeTimersConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    // Comfortably below ResumeTimers' first_frame_timeout_seconds=2s -- proves the metadata came
    // from the immediate "first frame decided non-resume" path in rtmt.py's from_client_to_server
    // (resume_decided.set() + announce_fresh() run inline, no sleep), not from first_frame_deadline's
    // 2s fallback timer. (PR #54 review: the timeout used to be 0.5s with only a 400ms bound --
    // ~100ms of margin under load. Raised to 2s/1000ms for real headroom.)
    private static readonly TimeSpan WellUnderFirstFrameTimeout = TimeSpan.FromMilliseconds(1000);

    private static string RandomResumeLookingId() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); // 64 chars, in [32,128]

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_non_resume_first_frame_decides_fresh_immediately_not_after_the_timeout() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        var stopwatch = Stopwatch.StartNew();
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        stopwatch.Stop();

        Assert.True(metadata is not null, "Expected extension.session_metadata after a non-resume first frame.");
        Assert.True(stopwatch.Elapsed < WellUnderFirstFrameTimeout,
            $"Metadata took {stopwatch.Elapsed} -- expected the immediate first-frame decision path, " +
            $"not the {WellUnderFirstFrameTimeout} first-frame-timeout fallback.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.True(!string.IsNullOrEmpty(resumeId));
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_late_resume_attempt_is_rejected_and_the_session_continues() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // First frame: not a resume, decides "fresh" for this socket.
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var firstMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(firstMetadata is not null, "Expected extension.session_metadata after the first frame.");

        // Second frame: extension.resume is no longer eligible -- only the first frame is.
        await browser.SendExtensionResumeAsync(RandomResumeLookingId(), ct);
        var rejected = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected" && f.Sequence > firstMetadata.Sequence, FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for a late (non-first-frame) resume attempt.");
        Assert.Equal("not_first_frame", rejected!.Json.GetProperty("reason").GetString());

        // The browser drops its stored id on any rejection, so the socket re-announces its own
        // (already-running) session with a freshly rotated id -- proof the session itself was
        // never torn down by the rejected resume attempt.
        var freshMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected a fresh re-announce after the late-resume rejection.");
        var firstResumeId = firstMetadata.Json.GetProperty("resumeId").GetString();
        var freshResumeId = freshMetadata!.Json.GetProperty("resumeId").GetString();
        Assert.NotEqual(firstResumeId, freshResumeId);

        // The socket itself was never closed by the rejected attempt: no close observed shortly after.
        // (Not a Task.WhenAny of two identical 500ms timers -- ContinueWith runs regardless of
        // whether its antecedent ran to completion or faulted, so a WhenAny between "close-wait
        // times out" and "plain delay elapses" is itself a coin-flip race between two ~500ms
        // clocks, not a real check. WaitForCloseAsync's own TimeoutException already *is* the
        // "no close" signal, so catch it directly instead.)
        var closedQuickly = true;
        try
        {
            await browser.WaitForCloseAsync(TimeSpan.FromMilliseconds(500), ct);
        }
        catch (TimeoutException)
        {
            closedQuickly = false;
        }
        Assert.False(closedQuickly, "The session must continue normally after a rejected late resume, not close.");
    });

    /// <summary>
    /// Rick's #244 review (issue 2, HIGH): the above test only covers a *structurally* late
    /// resume (a second frame after a non-resume first frame already decided "fresh" inline).
    /// rtmt.py's own late-resume check (~2586, 2592-2593) is actually against
    /// `resume_decided.is_set()`, which the first_frame_timeout_seconds fallback ALSO sets when it
    /// fires with no first frame at all. Before this fix, HandleResumeFirstFrameAsync had no
    /// `state.FirstFrameDecision.Task.IsCompleted` guard, so an `extension.resume` arriving after
    /// the 2s timeout fallback already decided "fresh" (ResumeTimers' own
    /// CONFORMANCE_FIRST_FRAME_TIMEOUT_SECONDS=2) would still be treated as a legitimate
    /// first-frame resume attempt instead of being routed through the late-resume rejection --
    /// accepting a resume well after the socket's own fresh identity was already established and
    /// announced. This test lets the timeout fallback fire first (no frame sent at all, not even a
    /// non-resume one), confirms the resulting extension.session_metadata genuinely took the ~2s
    /// timeout path (not the inline immediate-decision path WellUnderFirstFrameTimeout guards
    /// against), then sends extension.resume and asserts it gets the exact same
    /// not_first_frame rejection, fresh re-announce, and uninterrupted session as a structurally
    /// late resume.
    ///
    /// Mutation check: removing the IsCompleted guard in RealtimeProcessor.cs's checkingFirstFrame
    /// block makes this test fail (the resume is instead accepted as this connection's first
    /// frame: no extension.resume_rejected is ever observed within FrameTimeout).
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_resume_sent_after_the_first_frame_timeout_fallback_is_rejected_as_late() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // Deliberately send nothing: the only way "fresh" gets decided here is the
        // first_frame_timeout_seconds=2s fallback timer itself (rtmt.py's first_frame_deadline),
        // not an inline client-frame decision.
        var stopwatch = Stopwatch.StartNew();
        var firstMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        stopwatch.Stop();
        Assert.True(firstMetadata is not null, "Expected extension.session_metadata from the first-frame-timeout fallback.");
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(1800),
            $"Metadata arrived after only {stopwatch.Elapsed} with no client frame ever sent -- expected it to come " +
            "from the ~2s first_frame_timeout_seconds fallback, not an inline decision (which would imply a frame " +
            "was decided upon that was never actually sent).");

        // Now send extension.resume -- genuinely this connection's first OUTGOING client frame,
        // but the timeout fallback already decided "fresh" for this socket. Must be rejected the
        // same way as a structurally late resume, not accepted as a legitimate first-frame resume.
        await browser.SendExtensionResumeAsync(RandomResumeLookingId(), ct);
        var rejected = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected" && f.Sequence > firstMetadata.Sequence, FrameTimeout, ct);
        Assert.True(rejected is not null,
            "Expected extension.resume_rejected for a resume sent after the first-frame-timeout fallback already decided fresh.");
        Assert.Equal("not_first_frame", rejected!.Json.GetProperty("reason").GetString());

        var freshMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected a fresh re-announce after the late-resume rejection.");
        var firstResumeId = firstMetadata.Json.GetProperty("resumeId").GetString();
        var freshResumeId = freshMetadata!.Json.GetProperty("resumeId").GetString();
        Assert.NotEqual(firstResumeId, freshResumeId);

        var closedQuickly = true;
        try
        {
            await browser.WaitForCloseAsync(TimeSpan.FromMilliseconds(500), ct);
        }
        catch (TimeoutException)
        {
            closedQuickly = false;
        }
        Assert.False(closedQuickly, "The session must continue normally after the rejected timeout-race resume, not close.");
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_malformed_resume_id_as_the_first_frame_is_rejected() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // Well under session_manager.py's _RESUME_ID_MIN_LEN=32 -- rejected before any lookup.
        await browser.SendExtensionResumeAsync("too-short", ct);

        var rejected = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for a malformed resume id.");
        Assert.Equal("malformed", rejected!.Json.GetProperty("reason").GetString());

        var freshMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected fresh extension.session_metadata after the malformed resume rejection.");
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task An_unknown_resume_id_as_the_first_frame_is_rejected() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        // Right length (64 chars, inside [32,128]) but never issued by this (or any) backend run.
        await browser.SendExtensionResumeAsync(RandomResumeLookingId(), ct);

        var rejected = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected extension.resume_rejected for an unknown resume id.");
        Assert.Equal("unknown", rejected!.Json.GetProperty("reason").GetString());

        var freshMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejected.Sequence, FrameTimeout, ct);
        Assert.True(freshMetadata is not null, "Expected fresh extension.session_metadata after the unknown resume rejection.");
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_resume_id_is_single_use_a_second_attempt_with_the_same_id_is_unknown() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var firstMetadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(firstMetadata is not null);
        var originalResumeId = firstMetadata!.Json.GetProperty("resumeId").GetString();

        // Drop without extension.end_session -- a genuine, resumable detach.
        await browser.CloseAsync(cancellationToken: ct);
        await browser.WaitForCloseAsync(FrameTimeout, ct);
        await browser.DisposeAsync();

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(originalResumeId!, ct);

        var resumed = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected the first resume attempt (id never used before) to succeed.");

        // The presented id was popped before a rotated one was issued -- using the SAME
        // (now-consumed) original id again must be "unknown", never "expired" (which would
        // imply the id was still recognised as belonging to a session).
        var thirdConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var third = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await thirdConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await third.SendExtensionResumeAsync(originalResumeId!, ct);

        var rejected = await third.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected", FrameTimeout, ct);
        Assert.True(rejected is not null, "Expected the second attempt with the already-consumed resume id to be rejected.");
        Assert.Equal("unknown", rejected!.Json.GetProperty("reason").GetString());
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task A_stray_late_resume_on_an_already_resumed_connection_still_gets_a_rotated_re_announce() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        // Establish a genuinely resumable session, then detach without extension.end_session.
        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var original = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await original.SendStartSessionAsync(cancellationToken: ct);
        var originalMetadata = await original.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(originalMetadata is not null);
        var resumeId = originalMetadata!.Json.GetProperty("resumeId").GetString();
        await original.CloseAsync(cancellationToken: ct);
        await original.WaitForCloseAsync(FrameTimeout, ct);
        await original.DisposeAsync();

        // This connection's own first frame IS the resume -- it succeeds.
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var resumedConnection = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await resumedConnection.SendExtensionResumeAsync(resumeId!, ct);
        var resumed = await resumedConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected the resume to succeed.");
        var resumedResumeId = resumed!.Json.GetProperty("resume_id").GetString();

        // A stray second extension.resume on this already-resumed connection is rejected as
        // "not_first_frame" -- rtmt.py's handle_resume() sets its `announced` nonlocal true on a
        // successful resume (same as the fresh-connection path), so reject_late_resume's
        // `if announced: ... announce_fresh()` DOES fire here: the browser dropped its stored id on
        // the rejection, so this socket's own session gets a freshly ROTATED resume id, exactly like
        // a fresh connection would. (Confirmed against the real Python backend -- an earlier draft
        // of this test incorrectly assumed a resumed connection is exempt from the re-announce;
        // it is not.)
        await resumedConnection.SendExtensionResumeAsync(RandomResumeLookingId(), ct);
        var rejectedOnResumedSocket = await resumedConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.resume_rejected" && f.Sequence > resumed.Sequence, FrameTimeout, ct);
        Assert.True(rejectedOnResumedSocket is not null,
            "Expected extension.resume_rejected for a stray resume attempt on an already-resumed connection.");
        Assert.Equal("not_first_frame", rejectedOnResumedSocket!.Json.GetProperty("reason").GetString());

        var reannounced = await resumedConnection.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata" && f.Sequence > rejectedOnResumedSocket.Sequence,
            FrameTimeout, ct);
        Assert.True(reannounced is not null,
            "Expected a rotated extension.session_metadata re-announce after the stray late resume " +
            "on an already-resumed connection -- Python's handle_resume() sets `announced = true` on " +
            "a successful resume exactly like the fresh path does, so this connection already holds " +
            "a resume-id baton that must be rotated once the browser drops its old one.");
        var rotatedResumeId = reannounced!.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrEmpty(rotatedResumeId));
        Assert.NotEqual(resumedResumeId, rotatedResumeId);

        // The session itself must still be untouched by the stray attempt.
        var closedQuickly = true;
        try
        {
            await resumedConnection.WaitForCloseAsync(TimeSpan.FromMilliseconds(500), ct);
        }
        catch (TimeoutException)
        {
            closedQuickly = false;
        }
        Assert.False(closedQuickly, "The resumed session must continue normally after the stray late resume, not close.");
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Resuming_from_a_still_attached_socket_supersedes_it_with_4002() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var first = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await firstConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await first.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await first.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null);
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();

        // `first` is deliberately left open (never closed) -- session_manager.py's resume() does
        // not require a detach: it only checks the digest and the idle-timeout window, so a
        // still-attached session's resume id is equally valid. This is the "steal" path.
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);

        var resumed = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected the still-attached session's resume id to be honoured.");

        // rtmt.py's handle_resume spawns _close_superseded(stale_ws) as a background task once
        // the resume completes -- the old socket's close is asynchronous, not synchronous with
        // the resume frame itself. This baseline covers the well-behaved-peer path end to end
        // (including the actual 4002 close status); see
        // Resuming_from_a_still_attached_socket_whose_transport_cannot_drain_still_forwards_the_new_sockets_own_session_update_promptly
        // below for the non-draining-peer backpressure hazard from Rick's round-2 review of #244,
        // which can't also observe CloseStatus for reasons explained on that test.
        await first.WaitForCloseAsync(FrameTimeout, ct);
        Assert.Equal((WebSocketCloseStatus)4002, first.CloseStatus);
        Assert.Equal("superseded", first.CloseStatusDescription);
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Resuming_from_a_still_attached_socket_whose_transport_cannot_drain_still_forwards_the_new_sockets_own_session_update_promptly() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var firstConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var first = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var firstUpstream = await firstConnectionTask;
        Assert.True(firstUpstream is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await first.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await first.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null);
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();

        // Rick's round-2 review of #244 (issue 1, BLOCKING): `first`'s own socket is deliberately
        // made a NON-DRAINING half-open peer, not merely a stuck-but-idle one (CloseCodeTests'
        // own Superseding_a_stuck_peer_that_never_acks_the_close_still_completes_promptly already
        // covers that lighter case, and already passed even against the pre-fix code, because a
        // single ~tiny 4002 close frame always fits in the kernel send buffer regardless of
        // whether the peer reads). Stopping the reader loop (so `first` can never drain anything,
        // exactly like a phone that lost signal mid-response) and then flooding its own upstream
        // connection with a burst of large assistant-audio deltas -- the backend forwards every
        // one of these to `first`'s real socket as fast as they arrive -- is an attempt to build up
        // genuine backlog on that socket's own outbound send path, the only way to reproduce a send
        // that can actually block (not just fail to get an application-level ack).
        //
        // HONEST LIMITATION: empirically, even a sustained 5s/1MB-chunk flood over this dev box's
        // loopback TCP did not reliably make CloseOutputAsync actually block long enough for this
        // test to fail against the un-fixed (inline-await) code -- Windows loopback autotuning and
        // Kestrel's own pipe buffering apparently absorb it within the idle-timeout-safe window
        // available here (see the delay below). This test is kept as a best-effort, real-mechanism
        // regression guard (it still exercises a reader-stopped peer and asserts on the actual
        // thing Rick's review asked for -- B's own forwarding, not just session_resumed -- which is
        // a strictly stronger assertion than the old version had), but it is NOT the proof that the
        // blocking fix works: that proof is
        // CloseSupersededStaleConnectionAsyncTests.Completes_within_its_own_timeout_even_when_the_stale_sockets_close_output_hangs_forever
        // in Backend.Tests, which deterministically mutation-checks the exact production code path
        // (confirmed failing against the un-fixed code, passing against the fix).
        first.StopReaderLoopForTesting();
        var floodDelta = Convert.ToBase64String(new byte[1024 * 1024]);
        using var floodCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var floodTask = Task.Run(async () =>
        {
            try
            {
                while (!floodCts.IsCancellationRequested)
                {
                    await firstUpstream!.SendAsync(new JsonObject
                    {
                        ["type"] = "response.audio.delta",
                        ["response_id"] = "resp_flood",
                        ["item_id"] = "item_flood",
                        ["delta"] = floodDelta,
                    }, floodCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: floodCts is cancelled once the assertions below are done with it.
            }
            catch (Exception)
            {
                // Once `first` is actually torn down (post-fix, via the background supersede
                // close, or even pre-fix if the stale connection's OWN relay loop notices
                // something first) further sends against its upstream connection can legitimately
                // fail -- this flood's only job is to build up backlog while it can, not to
                // outlive the connection it's flooding.
            }
        }, ct);

        // Give the flood a head start to build up genuine backlog on `first`'s own send path
        // before triggering the resume -- a single flood iteration or two (as proven by an earlier
        // run of this test, which passed in ~650ms even against the pre-fix code) isn't nearly
        // enough: localhost loopback socket buffers are large, so it takes a real burst of
        // sustained, undrained sends to actually fill them and make the eventual CloseOutputAsync
        // call block.
        // Keep comfortably under ResumeTimers' CONFORMANCE_IDLE_TIMEOUT_SECONDS=8 (idle timeout
        // applies even to a still-attached session, and nothing in this flood touches activity --
        // by design, matching the same "audio deltas don't count as activity" rule this round's own
        // fix 1 relies on) -- long enough to build real backlog, short enough that the idle checker
        // doesn't delete the session out from under the resume attempt.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        // `first` is deliberately left open (never closed) -- session_manager.py's resume() does
        // not require a detach: it only checks the digest and the idle-timeout window, so a
        // still-attached session's resume id is equally valid. This is the "steal" path.
        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var secondUpstream = await secondConnectionTask;
        Assert.True(secondUpstream is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);

        var resumed = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected the still-attached session's resume id to be honoured.");

        // The proof this test targets (round-2 review, issue 1): extension.session_resumed itself
        // is announced from a SEPARATE task woken by state.FirstFrameDecision resolving (see
        // AnnounceAfterFirstFrameDecisionAsync), decoupled from whatever HandleResumeFirstFrameAsync
        // does AFTERWARD on ITS OWN call stack -- so asserting only "resumed is not null" (the old
        // version of this test) does not actually prove the new connection's OWN forwarding is
        // unblocked. B's very next frame (its own startSession's session.update, sent right after
        // the resume) rides that SAME call stack: if the supersede path still awaited `first`'s
        // stuck close inline AND that close actually blocked, this send would be stuck behind it.
        var watermark = secondUpstream!.ReceivedFrames.Snapshot().Count;
        var stopwatch = Stopwatch.StartNew();
        await second.SendStartSessionAsync(cancellationToken: ct);
        var secondSessionUpdate = await secondUpstream.ReceivedFrames.WaitForAsync(
            f => f.Sequence >= watermark && f.Type == "session.update", FrameTimeout, ct);
        stopwatch.Stop();
        Assert.True(secondSessionUpdate is not null, "Expected B's own session.update to reach its upstream connection.");
        Assert.True(stopwatch.Elapsed < WellUnderFirstFrameTimeout,
            $"B's own session.update took {stopwatch.Elapsed} to reach upstream -- expected well under " +
            $"{WellUnderFirstFrameTimeout}; a slower time means B's own relay loop is still gated behind " +
            "A's stuck supersede-close, exactly the hazard Rick's round-2 review of #244 found.");

        // Deliberately NOT asserting first.CloseStatus == 4002 here: StopReaderLoopForTesting()
        // cancelled first's own reader loop, and that same loop is the only thing that ever
        // observes an incoming Close frame and records its status (see PumpReceivedFramesAsync) --
        // its cancellation unconditionally resolves WaitForCloseAsync's task via a `finally`,
        // indistinguishable from an actual close, with CloseStatus left null. Proving the stale
        // peer eventually receives an actual 4002 against a well-behaved (reading) peer is already
        // covered by this class's own simpler resume test above; this test's job is specifically
        // the backpressure hazard, which requires a peer that never drains, which is exactly what
        // makes watching its own close frame impossible. Stop the flood so the background close (or
        // the eventual idle teardown) isn't fighting an endless stream of new sends as cleanup runs.
        floodCts.Cancel();
        await floodTask;
    });

    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Resume_id_never_appears_in_backend_diagnostics() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await connectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await browser.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null);
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        Assert.True(!string.IsNullOrEmpty(resumeId));

        await browser.CloseAsync(cancellationToken: ct);
        await browser.WaitForCloseAsync(FrameTimeout, ct);
        await browser.DisposeAsync();

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var second = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await second.SendExtensionResumeAsync(resumeId!, ct);
        var resumed = await second.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null);

        // The harness's own client never puts the resume id anywhere but a JSON WebSocket frame
        // (SendExtensionResumeAsync) -- there is no HTTP endpoint or URL parameter for it anywhere
        // in the wire protocol, so "never in any URL" is true by construction. This asserts the
        // complementary half of session_manager.py's own contract ("the raw value goes to the
        // browser over the socket and nowhere else (never in URLs or logs)") against the backend's
        // own captured diagnostics/log output.
        var diagnostics = fixture.Backend!.DumpDiagnostics();
        Assert.DoesNotContain(resumeId!, diagnostics, StringComparison.Ordinal);
    });

    /// <summary>
    /// Rick's #244 review (issue 5): session_token/round_trip_index/round_trip_token must carry
    /// over from the ORIGINAL session on a resume, not reset -- app/backend/rtmt.py's
    /// handle_resume reuses the original session's own SessionIdentifiers object exactly
    /// (session_manager.py's resume() hands the existing session record, identifiers and all,
    /// back to the new connection), so a browser's own round-trip bookkeeping survives a
    /// reconnect exactly the same way the order does. extension.session_metadata /
    /// extension.round_trip_token use camelCase (sessionToken/roundTripIndex/roundTripToken,
    /// SessionIdentifiers.ToFrame) while extension.session_resumed uses snake_case
    /// (session_token/round_trip_index/round_trip_token) -- both names for the exact same
    /// underlying identifiers, so this asserts across that naming boundary.
    /// </summary>
    [Fact]
    [Trait("Dotnet", "ready")]
    public Task Resuming_carries_over_the_original_sessions_token_and_round_trip_state() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        var oldBrowser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await connectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await oldBrowser.SendStartSessionAsync(cancellationToken: ct);
        var metadata = await oldBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata after the first frame.");
        var resumeId = metadata!.Json.GetProperty("resumeId").GetString();
        var originalSessionToken = metadata.Json.GetProperty("sessionToken").GetString();
        Assert.False(string.IsNullOrEmpty(originalSessionToken));
        Assert.Equal(0, metadata.Json.GetProperty("roundTripIndex").GetInt32());

        // The greeting is one full round trip (a non-tool response.done), which AdvanceRoundTrip()s
        // the SAME SessionIdentifiers instance to index 1 -- this is the state that must survive
        // the resume below, not be reset back to 0 as if this were a brand new session.
        var greetingRoundTrip = await oldBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");
        Assert.Equal(1, greetingRoundTrip!.Json.GetProperty("roundTripIndex").GetInt32());
        var originalRoundTripToken = greetingRoundTrip.Json.GetProperty("roundTripToken").GetString();
        Assert.Equal($"{originalSessionToken}-0001", originalRoundTripToken);

        await oldBrowser.CloseAsync(cancellationToken: ct);
        await oldBrowser.WaitForCloseAsync(FrameTimeout, ct);
        await oldBrowser.DisposeAsync();

        var secondConnectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var newBrowser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        Assert.True(await secondConnectionTask is not null, $"No upstream connection was accepted within {FrameTimeout}.");
        await newBrowser.SendExtensionResumeAsync(resumeId!, ct);
        var resumed = await newBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, "Expected extension.session_resumed on a valid resume.");

        Assert.Equal(originalSessionToken, resumed!.Json.GetProperty("session_token").GetString());
        Assert.Equal(1, resumed.Json.GetProperty("round_trip_index").GetInt32());
        Assert.Equal(originalRoundTripToken, resumed.Json.GetProperty("round_trip_token").GetString());
    });
}

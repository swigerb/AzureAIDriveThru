using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// #8 barge-in: the browser's `response.cancel` (sent when the guest starts talking over the
/// assistant) is relayed upstream unchanged -- rtmt.py's `from_client_to_server` has no special
/// case for `response.cancel`, it passes straight through like any other client frame -- and the
/// resulting `response.done` with `status: "cancelled"` is handled by the same `response.done`
/// case as any other completion (round-trip token emitted, no crash, no special-cased error path),
/// so the round trip continues normally afterward. See <see cref="ResponseCancelTests"/> for the
/// fake-only GA cancel semantics this scenario builds on, and
/// <see cref="ResponseDoneRoundTripTests"/> for the real-backend round-trip-token pattern.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class ResponseCancelRelayTests(ConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public Task Browser_response_cancel_mid_stream_is_relayed_and_cancelled_status_is_handled_cleanly() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Let the greeting's own response.create/response.done round trip fully complete first --
        // enqueuing our script any earlier would let the automatic greeting consume it instead of
        // the turn we script and cancel below.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        // First delta arrives immediately (so the browser can observe genuine mid-stream state);
        // the second is paced 2s out, giving a wide, non-racy window to send response.cancel
        // while the response is still active, exactly like ResponseCancelTests' fake-only version
        // of this same scenario.
        connection!.Script.Enqueue(new ResponseScript(
        [
            new AudioDeltaEvent("YmFyZ2UtaW4tZmlyc3Q="),
            new AudioDeltaEvent("YmFyZ2UtaW4tc2Vjb25k", Pace: TimeSpan.FromSeconds(2)),
            new DoneEvent(),
        ]));

        await browser.SendResponseCreateAsync(ct);

        // rtmt.py's from_server_to_client relay renames GA audio-delta events to their legacy
        // shape for browser compatibility (_GA_TO_LEGACY_EVENTS in audio_pipeline.py:
        // "response.output_audio.delta" -> "response.audio.delta"), so the browser -- unlike a
        // client that talks directly to the fake upstream, see ResponseCancelTests -- observes
        // the legacy name here.
        var firstDelta = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > greetingRoundTrip!.Sequence && f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(firstDelta is not null, "Expected the first response.audio.delta to reach the browser (genuinely mid-stream) before cancelling.");

        // The barge-in itself: the browser sends response.cancel with the response still active --
        // rtmt.py relays it upstream with no special handling of its own.
        await browser.SendResponseCancelAsync(ct);

        var done = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.done" && f.Sequence > firstDelta!.Sequence, FrameTimeout, ct);
        Assert.True(done is not null, "Expected a response.done to reach the browser after response.cancel.");
        Assert.Equal("cancelled", done!.Json.GetProperty("response").GetProperty("status").GetString());

        // "the backend handles response.done status:cancelled" -- proven by a normal
        // extension.round_trip_token still arriving for this turn (the same case arm that
        // handles every other response.done also emits this, so a crash or a special-cased
        // swallow would show up here as a missing/blocked token) and by the connection staying
        // open and usable for a further, completely unrelated turn. NOTE: rtmt.py's
        // _process_message_to_client sends the round-trip token (session_manager.py
        // emit_session_identifiers) *before* returning the rewritten response.done payload to
        // its caller, which only then relays response.done itself -- so on the wire the token
        // genuinely arrives before response.done for the same turn. Both are therefore bounded
        // independently off firstDelta rather than chained off each other.
        var cancelledRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token" && f.Sequence > firstDelta!.Sequence, FrameTimeout, ct);
        Assert.True(cancelledRoundTrip is not null,
            "Expected extension.round_trip_token for the cancelled turn -- the round trip must continue normally, not be dropped or crash the connection.");

        Assert.Null(browser.CloseStatus);

        await browser.SendResponseCreateAsync(ct);
        var lastSeenSequence = Math.Max(done!.Sequence, cancelledRoundTrip!.Sequence);
        var nextRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token" && f.Sequence > lastSeenSequence, FrameTimeout, ct);
        Assert.True(nextRoundTrip is not null,
            "Expected the session to remain fully usable for a further turn after the barge-in.");

        // The "backend logged no unhandled error during this scenario" invariant (PR #22 review
        // item N5) is enforced fixture-wide by ConformanceFixture.RunAsync.
    });

    /// <summary>
    /// Issue #95: a racy `response.cancel` that lands strictly after the response it targets has
    /// already finished (e.g. useRealtime.tsx's unconditional cancel firing on a barge-in or a mic
    /// restart that loses the race against response.done) is rejected upstream with
    /// `response_cancel_not_active`, exactly like <see
    /// cref="ResponseCancelTests.Cancel_with_nothing_active_is_rejected_and_the_session_remains_unaffected"/>
    /// proves at the fake-only level. Through the real backend this is benign, not a backend
    /// fault -- rtmt.py logs it at INFO, not ERROR, so it is never counted against
    /// <see cref="ConformanceFixture.RunAsync(Func{Task})"/>'s default zero-tolerance backend-error
    /// baseline. This is the deterministic conformance row for the flake originally seen in
    /// OrderResumeBrowserTests' strict-autoplay scenario (PR #94, run 36294099810).
    /// </summary>
    [Fact]
    public Task Browser_response_cancel_after_the_response_already_finished_is_not_a_backend_error() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var noneOpen = await fixture.Realtime.WaitForNoOpenConnectionsAsync(FrameTimeout, ct);
        Assert.True(noneOpen, $"Expected no open upstream connections at test start, but " +
            $"{fixture.Realtime.OpenConnectionCount} are still open — a previous test leaked a connection.");

        var connectionTask = fixture.Realtime.WaitForNextConnectionAsync(FrameTimeout, ct);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, cancellationToken: ct);
        var connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection was accepted within {FrameTimeout}.");

        await browser.SendStartSessionAsync(cancellationToken: ct);

        // Let the greeting's response.create/response.done finish fully first, then drive one
        // more turn to completion with ResponseScript.Default's unpaced (immediate) delta+done --
        // by the time the browser observes response.done, ActiveResponseId is already cleared
        // upstream, so a cancel sent afterward is guaranteed racy/late rather than mid-stream.
        var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(greetingRoundTrip is not null, "Greeting round trip never completed.");

        connection!.Script.Enqueue(ResponseScript.Default);
        await browser.SendResponseCreateAsync(ct);
        var done = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.done" && f.Sequence > greetingRoundTrip!.Sequence, FrameTimeout, ct);
        Assert.True(done is not null, "Expected the scripted turn's response.done to reach the browser before cancelling.");
        Assert.Equal("completed", done!.Json.GetProperty("response").GetProperty("status").GetString());

        // The race from #95: response.cancel sent unconditionally, after the response it would
        // have targeted has already finished. rtmt.py has no special case of its own for
        // response.cancel -- it relays straight through, same as the mid-stream case above.
        await browser.SendResponseCancelAsync(ct);

        var error = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "error" && f.Sequence > done!.Sequence, FrameTimeout, ct);
        Assert.True(error is not null, "Expected the fake upstream's rejection to reach the browser.");
        Assert.Equal("response_cancel_not_active", error!.Json.GetProperty("error").GetProperty("code").GetString());

        // The connection must stay open and usable -- a benign, expected rejection, not a fault.
        Assert.Null(browser.CloseStatus);
        await browser.SendResponseCreateAsync(ct);
        var nextRoundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "extension.round_trip_token" && f.Sequence > error!.Sequence, FrameTimeout, ct);
        Assert.True(nextRoundTrip is not null,
            "Expected the session to remain fully usable for a further turn after the benign cancel rejection.");

        // The actual regression check: ConformanceFixture.RunAsync's default zero-tolerance
        // backend-error baseline (allowedNewBackendErrors: 0) fails this test by itself if rtmt.py
        // logs response_cancel_not_active at ERROR -- no separate assertion needed here.
    });
}

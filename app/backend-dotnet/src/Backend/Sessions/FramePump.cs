using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Realtime;
using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Issue #338: the low-level, session-agnostic half of <see cref="RealtimeProcessor"/>'s relay --
/// best-effort socket close/drain helpers and the audio-append fast path -- extracted so
/// <see cref="RealtimeProcessor"/> and <see cref="CascadeProcessor"/> can share the exact same
/// supersede-close sequence instead of one calling into the other's internals.
///
/// Pure move-and-delegate: every method here is copied verbatim from
/// <see cref="RealtimeProcessor"/> (only the receiver changed, from an instance field to an
/// explicit parameter), so templates, levels, EventIds, lock scopes, <c>ConfigureAwait</c> and
/// cancellation-token flow are all unchanged. See <see cref="FramePumpLog"/> for the three log
/// methods that moved here with their call sites, keeping their original EventIds (1038-1040)
/// from <see cref="RealtimeProcessor"/>'s own reserved 1000-1999 range -- they are grandfathered,
/// not renumbered, because the conformance suite matches on log text/EventId and moving a log
/// call must never change either.
/// </summary>
internal static class FramePump
{
    /// <summary>Rick's #244 round-2 review, issue 1: bounds how long the BACKGROUND
    /// supersede-close (<see cref="CloseSupersededStaleConnectionAsync"/>) may spend trying to
    /// drain a courtesy close frame to a stale peer before giving up and cancelling its CTS
    /// anyway. Python's own <c>_close_superseded</c> (rtmt.py ~1026) has no timeout at all --
    /// it is already a background task, so an unbounded await never blocks anything else, and a
    /// stuck peer just leaks one background task/socket forever. This port chooses to bound it
    /// instead (a stuck peer's resources get reclaimed eventually), but the bound must be loose:
    /// CI run 37208960846 caught the first value here (2s) aborting the ordinary, healthy-peer
    /// <c>Resuming_from_a_still_attached_socket_supersedes_it_with_4002</c> conformance test
    /// under full-suite parallel load -- the close frame hadn't even failed to send, it simply
    /// hadn't finished within 2s of CPU-starved scheduling, so the timeout fired and aborted a
    /// peer that was never actually stuck. Widened to 10s, which stays comfortably inside that
    /// test's own 30s <c>FrameTimeout</c> while giving a merely-slow-under-load close far more
    /// room than a merely-busy CI runner should ever need, and still reclaims a truly
    /// never-draining peer (Rick's actual repro) in bounded time rather than Python's
    /// forever.</summary>
    internal static readonly TimeSpan SupersededCloseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Rick's #244 round-2 re-review: the poll interval
    /// <see cref="CloseSupersededStaleConnectionAsync"/> uses while waiting for the stale socket to
    /// settle (leave <see cref="WebSocketState.Open"/>/<see cref="WebSocketState.CloseSent"/>)
    /// before it cancels <c>staleCts</c>. Short enough that a healthy peer's near-instant answering
    /// close is noticed within a few polls (no perceptible delay added to the common case), long
    /// enough not to busy-spin the thread pool while waiting out a genuinely stuck peer for the
    /// rest of <see cref="SupersededCloseTimeout"/>.</summary>
    private static readonly TimeSpan SupersededSettlePollInterval = TimeSpan.FromMilliseconds(10);

    internal static async Task SwallowAsync(Task task, ILogger logger)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: linkedCts.Cancel() in the caller's finally is what unblocks the
            // counterpart loop's pending ReadMessageAsync/SendTextAsync in the first place.
        }
        catch (Exception ex)
        {
            // R3: this used to say "already logged inside the loop itself", which was wrong for a
            // send failure or any other exception the loop didn't itself expect and log -- that
            // exception surfaced here with nothing in the logs at all. Both relay loops now catch
            // and log their own per-frame failures, so reaching here at all means something above
            // the per-frame try/catch faulted (e.g. the loop's own setup) -- log it at Error so a
            // silently-ended session always leaves a trace.
            logger?.UnhandledRelayDrainException(ex);
        }
    }

    internal static async Task CloseIfOpenAsync(WebSocket socket, WebSocketCloseStatus status, string? description)
    {
        if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived)
        {
            return;
        }
        try
        {
            await socket.CloseAsync(status, description, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: the peer may have already torn the connection down.
        }
    }

    /// <summary>Rick's #244 review (issue 4) and round-2 review (issue 1): the send-only half of
    /// <see cref="CloseIfOpenAsync"/>, used wherever a socket's OWN relay loop may still have a
    /// <c>ReceiveAsync</c> pending on it concurrently (supersede; mirrors
    /// SessionManager.CloseIdleSessionsAsync's identical choice and doc comment for the idle-sweep
    /// case). <see cref="WebSocket.CloseOutputAsync"/> never waits for the peer's own handshake
    /// reply the way <see cref="WebSocket.CloseAsync"/> does, so it never contends with that
    /// pending receive -- but it is still a send, and a send can still block on a non-draining
    /// transport (half-open network-switch, full receive window) until there is buffer space or
    /// <paramref name="cancellationToken"/> fires; an EARLIER version of this comment claimed it
    /// "can never hang", which Rick's round-2 review (issue 1) disproved with a probe over exactly
    /// such a transport. Callers that cannot afford to be blocked by a stuck PEER (i.e. anywhere
    /// this runs inline in some OTHER connection's own call stack, like the supersede path) must
    /// pass a bounded token rather than <see cref="CancellationToken.None"/>.</summary>
    internal static async Task CloseOutputIfOpenAsync(
        WebSocket socket, WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived)
        {
            return;
        }
        try
        {
            await socket.CloseOutputAsync(status, description, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: the peer may have already torn the connection down, or (round-2 review,
            // issue 1) cancellationToken fired because the peer wasn't draining -- either way this
            // is the connection that's being superseded/dropped, so an incomplete close frame is
            // acceptable; the caller still cancels its CTS/tears it down regardless.
        }
    }

    /// <summary>Rick's #244 round-2 review, issue 1: runs the stale-socket close-and-cancel
    /// sequence that USED to sit inline in <c>HandleResumeFirstFrameAsync</c>, but now off the
    /// NEW (winning) connection's own call stack entirely -- see the call site's doc comment for
    /// why awaiting it there was unsafe. Bounded by <paramref name="closeTimeout"/> so a
    /// non-draining stale peer can delay this method's own completion by at most that long, never
    /// indefinitely; a cancelled <see cref="WebSocket.CloseOutputAsync"/> aborts the stuck send,
    /// which is an acceptable outcome for the connection that's losing anyway.
    ///
    /// Rick's round-2 RE-review (CI run 37210749254): widening <paramref name="closeTimeout"/>
    /// alone (2s -> 10s) was not the whole fix. <paramref name="staleCts"/> is the SAME token
    /// source the stale connection's own relay loop passed into its still-pending
    /// <c>browserSocket.ReceiveAsync</c> (it is reading for a NEXT client frame that will never
    /// come, since this peer just lost the race). Cancelling a token that's registered with an
    /// in-flight <see cref="WebSocket"/> receive/send does not just stop that one call -- per
    /// .NET's documented WebSocket cancellation semantics it ABORTS THE WHOLE SOCKET. Cancelling
    /// <paramref name="staleCts"/> immediately after the courtesy close frame was sent could abort
    /// <paramref name="staleWs"/> before the healthy stale peer's own answering close frame (which
    /// the very same pending <c>ReceiveAsync</c> is waiting to observe) had been processed,
    /// racing away the clean 4002 the peer would otherwise have seen -- exactly the failure mode
    /// behind <c>Resuming_a_still_attached_session_supersedes_the_original_socket_with_4002</c>'s
    /// "Expected 4002, Actual null" under CI load. So: after the close frame is away, wait for
    /// <paramref name="staleWs"/> to leave <see cref="WebSocketState.Open"/>/<see
    /// cref="WebSocketState.CloseSent"/> (i.e. for that already-in-flight receive to notice the
    /// peer's own close reply and complete on its own, harmlessly) before ever touching
    /// <paramref name="staleCts"/> -- reusing the SAME <paramref name="closeTimeout"/> budget so a
    /// genuinely stuck peer (never answers) is still bounded exactly as before. <paramref
    /// name="staleCts"/> is always cancelled in the <c>finally</c> once settled-or-timed-out,
    /// independent of whether the close itself completed, timed out, or threw -- a stale
    /// connection's relay loops must stop either way, this just makes sure that stop can never
    /// itself be the thing that drops the courtesy close frame.</summary>
    internal static async Task CloseSupersededStaleConnectionAsync(
        WebSocket staleWs,
        CancellationTokenSource? staleCts,
        TimeSpan closeTimeout,
        ILogger logger)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(closeTimeout);
            await CloseOutputIfOpenAsync(
                    staleWs, (WebSocketCloseStatus)SessionManager.SupersededCloseCode, SessionManager.SupersededCloseReason,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            await WaitForStaleSocketToSettleAsync(staleWs, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // CloseOutputIfOpenAsync and WaitForStaleSocketToSettleAsync already swallow their own
            // expected failures/timeouts; this guards the Task.Run itself (e.g. the
            // CancellationTokenSource construction) so a stray exception here can never prevent the
            // finally below from running.
            logger?.SupersededCloseFailed(ex);
        }
        finally
        {
            staleCts?.Cancel();
        }
    }

    /// <summary>Waits for <paramref name="staleWs"/> to leave <see cref="WebSocketState.Open"/> or
    /// <see cref="WebSocketState.CloseSent"/> -- i.e. for the stale connection's own already-pending
    /// <c>ReceiveAsync</c> to observe the peer's answering close frame (a healthy peer) and
    /// complete on its own, so the caller's subsequent <c>staleCts.Cancel()</c> never has to abort
    /// that receive mid-flight. Polls on <see cref="SupersededSettlePollInterval"/> rather than
    /// reacting to an event because <see cref="WebSocket"/> exposes no "state changed" signal; the
    /// socket is typically a <c>FakeWebSocket</c> or <c>ManagedWebSocket</c>, neither cheap nor
    /// meaningful to wrap further for this. Bounded by <paramref name="cancellationToken"/> (the
    /// SAME budget as the preceding close send) so a genuinely stuck peer that never answers still
    /// falls through to the unconditional cancel in the same overall bounded time as before this
    /// fix -- this method only ever makes the HEALTHY-peer path safer, never the stuck-peer path
    /// slower.</summary>
    private static async Task WaitForStaleSocketToSettleAsync(WebSocket staleWs, CancellationToken cancellationToken)
    {
        try
        {
            while (staleWs.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                await Task.Delay(SupersededSettlePollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Budget exhausted waiting for a peer that never answered -- the caller's finally
            // cancels staleCts regardless, which is the same "fine for the loser" outcome this
            // method existed to protect a HEALTHY peer from in the first place.
        }
    }

    internal static async Task SendTextAsync(WebSocket socket, string payload, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>Issue #13 Wave 2 audio-append fast path: forwards a frame's ORIGINAL bytes
    /// unchanged, skipping the UTF8-decode + re-encode round trip <see cref="SendTextAsync"/> does
    /// for a frame built from a <see cref="JsonObject"/>. Only ever called with
    /// <see cref="WebSocketFrame.Payload"/> itself, so "identical forwarded bytes" is exact, not
    /// just byte-equal after a round trip. Internal so
    /// <c>AudioAppendFastPathTests</c> can assert on exactly what reaches the socket without
    /// standing up a real upstream connection.</summary>
    internal static async Task SendBytesAsync(WebSocket socket, byte[] payload, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    /// <summary>Result of <see cref="TryAppendFastPath"/>: whether <c>payload</c>
    /// matched the fast-path shape at all, and if so, whether echo suppression says it must be
    /// dropped rather than forwarded.</summary>
    internal readonly record struct AppendFastPathResult(bool IsMatch, bool Suppressed);

    /// <summary>Issue #13 Wave 2: the audio-append fast path's full decision -- shape match plus
    /// the SAME echo-suppression gate the slow path applies (<c>state.Echo.ShouldSuppressAudio(...)</c>
    /// in <see cref="RealtimeProcessor.RunSessionAsync"/>) -- extracted to its own internal method
    /// (same idiom as <see cref="RealtimeProcessor.ResolveSessionBinding"/>/
    /// <see cref="RealtimeProcessor.ResolveUpstreamAuthHeaderAsync"/>) so a test can prove the
    /// gating without a live upstream socket.</summary>
    internal static AppendFastPathResult TryAppendFastPath(byte[] payload, EchoSuppressor echo, TimeProvider timeProvider)
    {
        if (!TryMatchAppendFastPath(payload))
        {
            return new AppendFastPathResult(IsMatch: false, Suppressed: false);
        }
        return new AppendFastPathResult(IsMatch: true, Suppressed: echo.ShouldSuppressAudio(NowSeconds(timeProvider)));
    }

    /// <summary>Rick's #229 review (round 2): the fast path's actual forward decision --
    /// <c>RelayBrowserToUpstreamAsync</c>'s <c>if (!fastPath.Suppressed)</c> branch -- extracted
    /// to its own internal method so a test can drive it end to end (real shape match, real
    /// echo-suppression gate, real forward) through a fake <see cref="WebSocket"/> stand-in for
    /// <paramref name="upstream"/>, without needing a live upstream connection. A no-op when
    /// <paramref name="fastPath"/> says the frame must be dropped (assistant still speaking);
    /// otherwise forwards <paramref name="payload"/>'s bytes completely unchanged, matching
    /// Python's own fast-path forward (errors are logged and swallowed, same as the slow path,
    /// since a single dropped audio frame must never tear down the whole session).</summary>
    internal static async Task ForwardFastPathAudioAsync(
        AppendFastPathResult fastPath, byte[] payload, WebSocket upstream, string sessionId, CancellationToken ct, ILogger logger)
    {
        if (fastPath.Suppressed)
        {
            return;
        }
        try
        {
            await SendBytesAsync(upstream, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.FastPathAudioForwardFailed(ex, sessionId);
        }
    }

    /// <summary>Issue #252 (Rick's review of #237's CI flake, same root cause independently
    /// diagnosed in this PR): <c>RelayBrowserToUpstreamAsync</c>'s send-with-bookkeeping step for
    /// every non-fast-path client→server frame, extracted to its own internal method (same idiom
    /// as <see cref="ForwardFastPathAudioAsync"/> above) so a test can drive the EXACT production
    /// ordering end to end through a fake <see cref="WebSocket"/> whose <c>SendAsync</c>
    /// synchronously simulates "the upstream's reply for this very frame was already fully
    /// processed before the send returns" -- precisely the interleaving a real race under load
    /// would produce -- without needing two concurrently-running relay loops racing for real.
    ///
    /// Recording "this response.create was browser-initiated" (<paramref name="rateLimit"/>'s
    /// <c>OnExternalResponseCreate("browser")</c>) and the symmetrical echo-suppression bookkeeping
    /// MUST happen before <paramref name="forwarded"/> is sent upstream, not after: the old order
    /// (send, then bookkeeping) left a TOCTOU window where upstream's own
    /// response.created/response.done for THIS SAME response.create could complete first --
    /// legitimately scheduling the ladder's first retry -- before this continuation resumed to make
    /// the bookkeeping call, which would then wrongly cancel the very retry it just caused
    /// (mistaking it for a stale leftover one). Recording "browser-initiated" before the send closes
    /// the window by construction: upstream cannot react to a frame it has not received yet.</summary>
    internal static async Task ForwardClientFrameAsync(
        JsonObject forwarded,
        string? sentType,
        EchoSuppressor echo,
        RateLimitRecovery rateLimit,
        WebSocket upstream,
        CancellationToken ct)
    {
        if (sentType == "response.create")
        {
            echo.OnExternalResponseCreate();
            rateLimit.OnExternalResponseCreate("browser");
        }

        await SendTextAsync(upstream, forwarded.ToJsonString(), ct).ConfigureAwait(false);

        if (sentType == "response.cancel")
        {
            echo.OnBargeIn();
        }
    }

    /// <summary>Port of app/backend/rtmt.py's <c>_CLIENT_APPEND_FAST_PATH_RE</c> (PR #49 round 2
    /// "M1"): the ONE exact byte shape useRealtime.tsx's <c>addUserAudio()</c> sends --
    /// <c>{"type":"input_audio_buffer.append","audio":"BASE64"}</c>, no <c>event_id</c>, no extra
    /// whitespace, no different key order. Deliberately anchored at both ends and over the whole
    /// payload (not a substring search): PR #49's own review history is why -- an earlier,
    /// unanchored substring fast path could be spoofed by embedding a fake
    /// <c>"type":"input_audio_buffer.append"</c> string inside a nested/arbitrary JSON value.
    /// Anything that doesn't match this exactly (an event_id, extra keys, a byte outside the
    /// base64 alphabet anywhere in the audio value, a trailing byte) falls through to the full
    /// parse + allow-list path below, which still accepts a genuine append frame in any other
    /// shape, just without the fast path's saved JSON-parse/rebuild/re-serialize work.</summary>
    private static readonly byte[] AppendFastPathPrefix =
        Encoding.ASCII.GetBytes("{\"type\":\"input_audio_buffer.append\",\"audio\":\"");
    private static readonly byte[] AppendFastPathSuffix = Encoding.ASCII.GetBytes("\"}");

    internal static bool TryMatchAppendFastPath(byte[] payload)
    {
        if (payload.Length < AppendFastPathPrefix.Length + AppendFastPathSuffix.Length)
        {
            return false;
        }
        if (!payload.AsSpan(0, AppendFastPathPrefix.Length).SequenceEqual(AppendFastPathPrefix))
        {
            return false;
        }
        var suffixStart = payload.Length - AppendFastPathSuffix.Length;
        if (!payload.AsSpan(suffixStart).SequenceEqual(AppendFastPathSuffix))
        {
            return false;
        }
        foreach (var b in payload.AsSpan(AppendFastPathPrefix.Length, suffixStart - AppendFastPathPrefix.Length))
        {
            if (!IsFastPathAudioAlphabetByte(b))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Deliberately matches rtmt.py's <c>_CLIENT_APPEND_FAST_PATH_RE</c> character class
    /// <c>[A-Za-z0-9+/=]</c> exactly, NOT <see cref="ClientServerFilter"/>'s stricter
    /// <c>^[A-Za-z0-9+/]*={0,2}$</c> slow-path audio regex (nor rtmt.py's own equally stricter
    /// <c>_CLIENT_BASE64_RE</c>): both languages' fast-path regexes allow a <c>=</c> anywhere in
    /// the value, any number of times, not just 0-2 trailing padding characters. This is a known,
    /// pre-existing looseness in the fast path versus the slow path in BOTH implementations (not
    /// introduced by this port) -- harmless, because the fast path only ever decides whether a
    /// frame takes the fast lane to the SAME unmodified upstream Azure OpenAI Realtime API, which
    /// independently validates/rejects malformed base64 itself; it never widens what the browser
    /// is allowed to do or what gets accepted as well-formed. Kept exactly as loose as Python's own
    /// fast path so the C# port's forwarding behaviour matches byte-for-byte, per this port's
    /// "match Python's handling exactly" requirement (issue #13).</summary>
    private static bool IsFastPathAudioAlphabetByte(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b == (byte)'+' || b == (byte)'/' || b == (byte)'=';

    /// <summary>Monotonic "loop time" in seconds, mirroring Python's
    /// <c>asyncio.AbstractEventLoop.time()</c> -- immune to system clock adjustments. Issue #13
    /// Wave 2: sourced from the injected <paramref name="timeProvider"/> (not
    /// <c>Environment.TickCount64</c>) so a <c>FakeTimeProvider</c>-backed test can advance it
    /// deterministically.</summary>
    private static double NowSeconds(TimeProvider timeProvider) =>
        timeProvider.GetTimestamp() / (double)timeProvider.TimestampFrequency;
}

using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Models;
using Backend.Personas;
using Backend.Realtime;
using Backend.Shared;
using Backend.Tools;
using Microsoft.Extensions.Logging;

namespace Backend.Sessions;

/// <summary>
/// Issue #338: the resume/supersede-announce collaborator extracted from
/// <see cref="RealtimeProcessor.RunSessionAsync"/> -- rtmt.py's own <c>handle_resume</c>,
/// <c>reject_late_resume</c> and <c>announce_fresh</c> local closures, plus the deferred
/// first-frame-decision announce (<c>AnnounceAfterFirstFrameDecisionAsync</c>), ported verbatim.
///
/// Pure move-and-delegate: every line below is unchanged from the original local functions except
/// that closed-over locals became constructor parameters -- including
/// <paramref name="buildVoiceUpdateFrame"/>, which stays a delegate onto
/// <c>RunSessionAsync</c>'s own <c>BuildVoiceUpdateFrame</c> local function (not duplicated here)
/// so a voice-frame shape change in one place can't drift from the other.
/// </summary>
internal sealed class ResumeCoordinator(
    WebSocket browserSocket,
    WebSocket upstream,
    Persona persona,
    ResolvedModel resolvedModel,
    string? menuMode,
    string sessionId,
    CancellationTokenSource linkedCts,
    RealtimeProcessor.RealtimeSessionState state,
    SessionManager? sessionManager,
    Func<string, JsonObject> buildVoiceUpdateFrame,
    ILogger logger,
    CancellationToken ct)
{
    // Issue #15: rejects an extension.resume that arrives too late to possibly win the
    // session -- either because it isn't structurally this connection's first frame at all
    // (HandleClientExtensionMessageAsync's call site), or because it IS the first frame but
    // state.FirstFrameDecision was already resolved by the timeout fallback racing ahead of
    // it (RelayBrowserToUpstreamAsync's checkingFirstFrame call site, Rick's #244 review
    // issue 2) -- both are the exact same "resume_decided already set" case Python's
    // reject_late_resume handles, so both now share this one rejection path instead of the
    // first-frame race silently falling through to HandleResumeFirstFrameAsync's destructive
    // (registry-mutating) resume logic.
    public async Task RejectLateResumeAsync(string logReason)
    {
        logger?.DroppedLateResume(logReason, sessionId);
        await FramePump.SendTextAsync(browserSocket, new JsonObject
        {
            ["type"] = "extension.resume_rejected",
            ["reason"] = "not_first_frame",
        }.ToJsonString(), ct).ConfigureAwait(false);

        // rtmt.py's reject_late_resume: the browser drops its stored id on ANY
        // rejection, so re-announce this socket's own session (with a rotated id) --
        // for any connection that already holds a resume-id baton, fresh or resumed
        // alike (MetadataAnnounced is set by both paths; see its doc comment). The only
        // connections that never set it are ones torn down before their first-frame
        // decision was ever reached.
        if (state.MetadataAnnounced)
        {
            await SendFreshSessionMetadataAsync().ConfigureAwait(false);
        }
    }

    // Issue #15: handles extension.resume when it IS this connection's own first frame (see
    // the isFirstFrame gate in RelayBrowserToUpstreamAsync -- a late resume is rejected there
    // via HandleClientExtensionMessageAsync instead, never here). Resolves
    // state.FirstFrameDecision exactly once either way, so AnnounceAfterFirstFrameDecisionAsync
    // (armed from the session.created handler) can proceed.
    //
    // Rick's #244 review (issue 2): callers MUST check state.FirstFrameDecision.Task.IsCompleted
    // before calling this -- it is never safe to call once that's already true (the timeout
    // fallback got there first), since every line below mutates the shared SessionManager
    // registry and this connection's own state unconditionally, regardless of whether
    // TrySetResult on an already-resolved TCS is a value no-op. See the class-level comment on
    // the timeout fallback above for the full rtmt.py parity reasoning.
    public async Task HandleResumeFirstFrameAsync(JsonObject message)
    {
        var presentedId = RealtimeProcessor.GetString(message, "resume_id");
        var outcome = sessionManager!.TryResume(
            browserSocket, presentedId, persona.Id, resolvedModel.Id, menuMode, sessionId, linkedCts,
            attachedSupersededFlag: state.Superseded);
        if (!outcome.Accepted)
        {
            logger?.ExtensionResumeRejected(outcome.Reason, sessionId);
            await FramePump.SendTextAsync(browserSocket, new JsonObject
            {
                ["type"] = "extension.resume_rejected",
                ["reason"] = outcome.Reason,
            }.ToJsonString(), ct).ConfigureAwait(false);
            state.FirstFrameDecision.TrySetResult(false);
            return;
        }

        logger?.SessionResumedWithId(outcome.SessionId, sessionId);
        state.EffectiveSessionId = outcome.SessionId!;
        state.ToolExecutor = outcome.ToolExecutor!;
        state.Voice = outcome.Voice!;
        // Rick's #244 review (issue 5): adopt the ORIGINAL session's identifiers object (same
        // reference, so its round_trip_index keeps counting up from where the prior
        // connection left off) instead of leaving state.Identifiers at the brand-new one this
        // connection minted before knowing whether it would end up resuming anything.
        if (outcome.Identifiers is { } originalIdentifiers)
        {
            state.Identifiers = originalIdentifiers;
        }
        // Issue #181: conversation_started gates whether this resume rehydrates silently
        // (greeting already happened -- GreetingSent=true suppresses GreetingGate.SendOnceAsync
        // entirely) or re-greets as if fresh. Only a rehydrating resume is nudge-eligible; a
        // resume before the greeting ever fired still greets normally and must not nudge on
        // top of that.
        state.GreetingSent = outcome.ConversationStarted;
        state.NudgeArmEligible = outcome.ConversationStarted;
        state.ResumeAnnounce = outcome;
        state.FirstFrameDecision.TrySetResult(true);

        if (outcome.StaleWs is { } staleWs)
        {
            // Rick's #244 round-2 review, issue 1: this USED to await the close-output send
            // and only THEN cancel outcome.StaleCts, all inline in THIS (the new, winning)
            // connection's own call stack -- reasoned (previous comment, now wrong) that
            // CloseOutputAsync "can never hang" because it never waits for the peer's
            // handshake reply. That is true of the HANDSHAKE wait, but CloseOutputAsync is
            // still a SEND, and a send can block on a half-open network-switch where the stale
            // socket's outbound isn't draining (e.g. assistant audio was streaming when the
            // network died) or its send lock is held -- Rick proved it with a probe over
            // exactly such a non-draining transport. Awaited inline here, that blocks not just
            // the stale connection's teardown but THIS connection's own first-frame handling
            // (and everything downstream of it), so the new socket forwards nothing -- no
            // session.update, no audio -- until an eventual 4000 idle close deletes the order,
            // even though the guest already saw session_resumed.
            //
            // Fixed the same way Python does it (rtmt.py's background `_close_superseded`,
            // ~1026): fire the close-and-cancel off as a background task with its own short
            // timeout, so it can never block this connection's own processing, while the 4002
            // close is still attempted promptly on a best-effort basis. The tool-dispatch race
            // this ordering previously depended on (StaleCts cancelled before another send
            // could race it) is now closed by state.Superseded instead -- TryResume marks the
            // STALE connection's own SupersededFlag synchronously under its lock the instant it
            // captures staleWs, independent of how long this background close later takes, and
            // HandleToolCallDoneAsync checks that flag (not StaleCts) before ever dispatching a
            // tool, because OrderToolExecutor.ExecuteAsync is synchronous, ignores its own
            // CancellationToken, and mutates OrderState, which isn't thread-safe -- a promptly
            // cancelled StaleCts alone was never enough to stop an already-started dispatch.
            Task.Run(
                () => FramePump.CloseSupersededStaleConnectionAsync(staleWs, outcome.StaleCts, FramePump.SupersededCloseTimeout, logger!),
                CancellationToken.None).FireAndForget(logger, nameof(FramePump.CloseSupersededStaleConnectionAsync));
        }
    }

    // Issue #15: reads an IOrderTicketSource best-effort, matching HandleToolCallDoneAsync's
    // own post-exception refresh pattern -- a session with no readable order state yet (or an
    // executor that throws on read) just gets "{}" instead of losing the whole announcement.
    private string SafeOrderSummaryJson(IOrderTicketSource source)
    {
        try
        {
            return source.CurrentOrderSummaryJson;
        }
        catch (Exception ex)
        {
            logger?.OrderStateReadForAnnouncementFailed(ex, sessionId);
            return "{}";
        }
    }

    // Issue #15: the deferred half of the session.created handler -- waits for this
    // connection's own first-frame decision (resume accepted/rejected/never attempted) before
    // telling the browser which it got. Ordering: the bootstrap session.update that
    // RunSessionAsync already sent upstream always precedes whatever this sends, since
    // session.created itself can only arrive after that connect/bootstrap completed.
    public async Task AnnounceAfterFirstFrameDecisionAsync()
    {
        bool resumed;
        try
        {
            resumed = await state.FirstFrameDecision.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session tore down (browser/upstream closed, cancellation requested) before any
            // first-frame decision was ever reached -- nothing left to announce.
            return;
        }

        try
        {
            if (resumed)
            {
                var outcome = state.ResumeAnnounce!;
                var orderSummaryJson = outcome.ToolExecutor is IOrderTicketSource ticketSource
                    ? SafeOrderSummaryJson(ticketSource)
                    : "{}";
                var resumedFrame = new JsonObject
                {
                    ["type"] = "extension.session_resumed",
                    ["order_summary"] = JsonNode.Parse(orderSummaryJson) ?? new JsonObject(),
                    ["session_token"] = state.Identifiers.SessionToken,
                    ["round_trip_index"] = state.Identifiers.RoundTripIndex,
                    // Rick's #244 review (issue 5): rtmt.py's handle_resume always includes
                    // round_trip_token alongside round_trip_index in this frame (App.tsx's own
                    // resume handling reads it, per types.ts's SessionResumedMessage) -- it was
                    // simply missing here even though SessionIdentifiers.RoundTripToken already
                    // exists as a computed property.
                    ["round_trip_token"] = state.Identifiers.RoundTripToken,
                    ["resume_id"] = outcome.ResumeId,
                };
                // Parity with rtmt.py's handle_resume (sets `announced = True` right before sending
                // extension.session_resumed): a successfully resumed connection holds a baton (its
                // own resume id) exactly like a fresh connection does, so if a later stray
                // extension.resume invalidates it, this socket must get the same rotated-id
                // re-announce a fresh connection would -- not silence.
                state.MetadataAnnounced = true;
                await FramePump.SendTextAsync(browserSocket, resumedFrame.ToJsonString(), ct).ConfigureAwait(false);

                if (outcome.ConversationStarted)
                {
                    var rehydrationItem = new JsonObject
                    {
                        ["type"] = "conversation.item.create",
                        ["item"] = new JsonObject
                        {
                            ["id"] = MiddleTierItemIds.NewId(),
                            ["type"] = "message",
                            ["role"] = "system",
                            ["content"] = new JsonArray(new JsonObject
                            {
                                ["type"] = "input_text",
                                ["text"] = SessionManager.BuildRehydrationText(
                                    orderSummaryJson,
                                    outcome.RecentTurns ?? Array.Empty<(string Role, string Text)>(),
                                    persona.RoleName),
                            }),
                        },
                    };
                    var rehydrationItemJson = rehydrationItem.ToJsonString();
                    await FramePump.SendTextAsync(upstream, rehydrationItemJson, ct).ConfigureAwait(false);
                    // Issue #13 tail: track the rehydration item in the context window, mirroring
                    // rtmt.py's ctx_monitor.add_content(rehydration) right after it's sent.
                    sessionManager?.GetContextMonitor(state.EffectiveSessionId)?.AddContent(rehydrationItemJson);

                    // Restore the persisted voice on the (brand new) upstream connection BEFORE any
                    // response.create can fire -- the bootstrap session.update already went out with
                    // whatever voice the fresh persona binding resolved to, so this corrects it in
                    // place (VoicePickerTests' resume-restore-precedes-response.create requirement).
                    var voiceUpdate = state.Guard.Track(buildVoiceUpdateFrame(state.Voice).ToJsonString());
                    await FramePump.SendTextAsync(upstream, voiceUpdate, ct).ConfigureAwait(false);
                }
            }
            else
            {
                await SendFreshSessionMetadataAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Best-effort fire-and-forget announce: teardown can race the post-decision send.
        }
        catch (WebSocketException)
        {
            // Best-effort fire-and-forget announce: a closing socket should not emit a fresh error log.
        }
    }

    // Port of rtmt.py's announce_fresh(): mints a rotated resumeId and (re-)announces
    // extension.session_metadata. Called once from AnnounceAfterFirstFrameDecisionAsync for a
    // genuinely fresh connection, and again -- for ANY connection that already holds a resume-id
    // baton (state.MetadataAnnounced, set by both the fresh path and a successful resume) -- from
    // a late (non-first-frame) extension.resume rejection, so the browser's dropped stored id is
    // replaced with a fresh one without re-greeting or otherwise disturbing the still-live
    // session.
    private async Task SendFreshSessionMetadataAsync()
    {
        var resumeId = sessionManager!.IssueResumeId(state.EffectiveSessionId);
        var metadataFrame = state.Identifiers.ToFrame("extension.session_metadata");
        if (resumeId is not null)
        {
            metadataFrame["resumeId"] = resumeId;
        }
        state.MetadataAnnounced = true;
        await FramePump.SendTextAsync(browserSocket, metadataFrame.ToJsonString(), ct).ConfigureAwait(false);
    }
}

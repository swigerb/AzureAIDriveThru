using System.Text.Json.Nodes;
using Backend.Realtime;

namespace Backend.Sessions;

/// <summary>
/// Issue #338: the resume/session-metadata wire-frame shapes that <see cref="RealtimeProcessor"/>
/// (via <see cref="ResumeCoordinator"/>) and <see cref="CascadeProcessor"/> already built
/// identically, field-for-field, in two places -- extracted here so the two pipelines can never
/// silently drift apart on the shape the browser extension parses.
///
/// Pure JSON construction only: no logging, no I/O, no session-registry mutation. Each caller
/// still does its own <c>SessionManager.TryResume</c>/<c>IssueResumeId</c> call and its own
/// (processor-specific, differently-EventId'd) logging around the result -- only the frame shape
/// itself is shared.
/// </summary>
internal static class ResumeEnvelope
{
    /// <summary>Builds the <c>extension.session_resumed</c> frame sent once a resume is accepted
    /// -- identical shape previously duplicated in
    /// <c>ResumeCoordinator.AnnounceAfterFirstFrameDecisionAsync</c> and
    /// <c>CascadeProcessor.NegotiateResumeAsync</c>'s accepted branch.
    ///
    /// Rick's #244 review (issue 5): rtmt.py's handle_resume always includes round_trip_token
    /// alongside round_trip_index in this frame (App.tsx's own resume handling reads it, per
    /// types.ts's SessionResumedMessage) -- round_trip_token must stay present here even though
    /// <see cref="SessionIdentifiers.RoundTripToken"/> is just a computed property derived from
    /// the other two fields.</summary>
    public static JsonObject BuildSessionResumedFrame(
        SessionIdentifiers identifiers, string orderSummaryJson, string? resumeId) => new()
    {
        ["type"] = "extension.session_resumed",
        ["order_summary"] = JsonNode.Parse(orderSummaryJson) ?? new JsonObject(),
        ["session_token"] = identifiers.SessionToken,
        ["round_trip_index"] = identifiers.RoundTripIndex,
        ["round_trip_token"] = identifiers.RoundTripToken,
        ["resume_id"] = resumeId,
    };

    /// <summary>Builds the <c>extension.resume_rejected</c> frame -- identical shape previously
    /// duplicated across <c>ResumeCoordinator.RejectLateResumeAsync</c> (literal
    /// <c>"not_first_frame"</c> reason), <c>ResumeCoordinator.HandleResumeFirstFrameAsync</c>'s
    /// rejected branch and <c>CascadeProcessor.NegotiateResumeAsync</c>'s rejected branch (both of
    /// the latter pass <c>outcome.Reason</c>).</summary>
    public static JsonObject BuildResumeRejectedFrame(string? reason) => new()
    {
        ["type"] = "extension.resume_rejected",
        ["reason"] = reason,
    };

    /// <summary>Builds the <c>extension.session_metadata</c> frame -- <see cref="SessionIdentifiers.ToFrame"/>
    /// plus the optional <c>resumeId</c> baton, identical to what
    /// <c>ResumeCoordinator.SendFreshSessionMetadataAsync</c> and <c>CascadeProcessor</c>'s own
    /// fresh-path/no-registry announces both already built inline.</summary>
    public static JsonObject BuildSessionMetadataFrame(SessionIdentifiers identifiers, string? resumeId)
    {
        var frame = identifiers.ToFrame("extension.session_metadata");
        if (resumeId is not null)
        {
            frame["resumeId"] = resumeId;
        }

        return frame;
    }
}

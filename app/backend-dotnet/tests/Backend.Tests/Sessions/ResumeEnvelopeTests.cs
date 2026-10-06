using Backend.Realtime;
using Backend.Sessions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #338: focused unit tests for <see cref="ResumeEnvelope"/>, the resume/session-metadata
/// wire-frame builder shared between <see cref="RealtimeProcessor"/> (via
/// <see cref="ResumeCoordinator"/>) and <see cref="CascadeProcessor"/>. Verifies the exact field
/// names/order the browser extension parses, so the two pipelines can't silently drift.
/// </summary>
public sealed class ResumeEnvelopeTests
{
    [Fact]
    public void BuildSessionResumedFrame_IncludesOrderSummaryAndIdentifiers()
    {
        var identifiers = new SessionIdentifiers("sonic", "gpt-realtime", sessionToken: "tok-1");
        identifiers.AdvanceRoundTrip();

        var frame = ResumeEnvelope.BuildSessionResumedFrame(identifiers, """{"items":[]}""", "resume-42");

        Assert.Equal("extension.session_resumed", frame["type"]!.GetValue<string>());
        Assert.Equal("tok-1", frame["session_token"]!.GetValue<string>());
        Assert.Equal(1, frame["round_trip_index"]!.GetValue<int>());
        Assert.Equal("tok-1-0001", frame["round_trip_token"]!.GetValue<string>());
        Assert.Equal("resume-42", frame["resume_id"]!.GetValue<string>());
        Assert.Empty(frame["order_summary"]!["items"]!.AsArray());
    }

    [Fact]
    public void BuildSessionResumedFrame_WithoutResumeId_OmitsItButKeepsKey()
    {
        var identifiers = new SessionIdentifiers("sonic", "gpt-realtime", sessionToken: "tok-2");

        var frame = ResumeEnvelope.BuildSessionResumedFrame(identifiers, "{}", resumeId: null);

        Assert.True(frame.ContainsKey("resume_id"));
        Assert.Null(frame["resume_id"]);
    }

    [Theory]
    [InlineData("not_first_frame")]
    [InlineData("already_active")]
    [InlineData(null)]
    public void BuildResumeRejectedFrame_CarriesReasonVerbatim(string? reason)
    {
        var frame = ResumeEnvelope.BuildResumeRejectedFrame(reason);

        Assert.Equal("extension.resume_rejected", frame["type"]!.GetValue<string>());
        if (reason is null)
        {
            Assert.Null(frame["reason"]);
        }
        else
        {
            Assert.Equal(reason, frame["reason"]!.GetValue<string>());
        }
    }

    [Fact]
    public void BuildSessionMetadataFrame_WithResumeId_AddsResumeIdOnTopOfToFrame()
    {
        var identifiers = new SessionIdentifiers("sonic", "gpt-realtime", sessionToken: "tok-3");

        var frame = ResumeEnvelope.BuildSessionMetadataFrame(identifiers, "resume-7");

        Assert.Equal("extension.session_metadata", frame["type"]!.GetValue<string>());
        Assert.Equal("tok-3", frame["sessionToken"]!.GetValue<string>());
        Assert.Equal("resume-7", frame["resumeId"]!.GetValue<string>());
    }

    [Fact]
    public void BuildSessionMetadataFrame_WithoutResumeId_OmitsResumeIdKeyEntirely()
    {
        var identifiers = new SessionIdentifiers("sonic", "gpt-realtime", sessionToken: "tok-4");

        var frame = ResumeEnvelope.BuildSessionMetadataFrame(identifiers, resumeId: null);

        Assert.False(frame.ContainsKey("resumeId"));
    }
}

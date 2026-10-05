using System.Linq;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// Issue #126: `cascade` parity rows for the three behaviors <c>docs/persona-architecture.md</c>
/// section 7.1 previously listed as "deferred to #126" -- resume (grace hold + rehydration), and
/// echo suppression after playback -- proven here the same way <see cref="CascadeConformanceTests"/>
/// proves tool calling/pricing parity: against the real fake STT/chat/TTS upstreams, through the
/// real client wire protocol, with no backend-specific assertions. The realtime pipeline's own
/// idle-nudge/resume rows live in
/// <c>Scenarios/Sessions/ResumeRehydrationAndNudgeTests.cs</c>/<c>ResumeHandshakeTests.cs</c> --
/// those assert on realtime's own upstream `conversation.item.create` shape (not reusable here;
/// cascade's rehydration/nudge text is a plain chat-completions message, never an upstream frame).
/// The idle-nudge itself is NOT repeated here: it is already covered end to end, deterministically
/// (no real wall-clock wait), by both backends' own unit suites
/// (<c>app/backend/tests/test_cascade_processor.py</c>'s <c>NudgeSchedulingTests</c> and
/// <c>Backend.Tests</c>'s <c>CascadeProcessorTests.RunSessionAsync_NudgeFiresAfterSilenceFollowingAResumedRehydration</c>/
/// <c>..._NudgeIsCancelledWhenTheGuestBargesInBeforeItFires</c>) -- this fixture's own
/// <c>CONFORMANCE_NUDGE_AFTER_SECONDS=45</c> override (see <see cref="CascadeConformanceFixture"/>'s
/// own doc comment) makes a real-time nudge-firing row here prohibitively slow for no additional
/// coverage.
/// </summary>
[Collection(CascadeConformanceCollection.Name)]
public sealed class CascadeResumeRehydrationAndNudgeTests(CascadeConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = CascadeScenarioHelpers.FrameTimeout;

    [Fact]
    public Task Cascade_resume_accepted_mid_conversation_rehydrates_and_suppresses_the_greeting() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;

        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
        var metadata = connection.Browser.ReceivedFrames.Snapshot().Single(f => f.Type == "extension.session_metadata");
        var resumeId = metadata.Json.GetProperty("resumeId").GetString();
        Assert.False(string.IsNullOrEmpty(resumeId));

        // A real guest turn before dropping the socket, so the resumed connection has something
        // of its own (beyond just the greeting) to rehydrate.
        fixture.Chat.EnqueueMessage(new() { ["role"] = "assistant", ["content"] = "Sure, one burger coming up!" });
        fixture.Realtime.NextTranscript = "I'd like a burger";
        await CascadeScenarioHelpers.SendGuestTurnAsync(connection.Browser, ct);
        var firstAnswer = await connection.Browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(firstAnswer is not null, $"Expected the first guest turn's own answer within {FrameTimeout}.");

        await connection.Browser.CloseAsync(cancellationToken: ct);
        await connection.Browser.WaitForCloseAsync(FrameTimeout, ct);
        await connection.Browser.DisposeAsync();

        // Resume -- a brand new connection, presenting the FIRST (and only) connection's resume
        // id as its own literal first client frame.
        var resumedBrowser = await CascadeScenarioHelpers.ConnectAsync(fixture, "gpt-5-mini", ct);
        await using var _ = resumedBrowser;
        await resumedBrowser.SendExtensionResumeAsync(resumeId!, ct);

        var resumed = await resumedBrowser.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_resumed", FrameTimeout, ct);
        Assert.True(resumed is not null, $"Expected extension.session_resumed within {FrameTimeout}.");
        // A fresh, single-use resume credential is rotated in on every accepted resume -- never
        // the one just presented.
        Assert.NotEqual(resumeId, resumed!.Json.GetProperty("resume_id").GetString());

        // The resumed connection must never re-greet: no second extension.session_metadata, no
        // response.audio_transcript.delta until the guest actually speaks again.
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        var resumedFramesSoFar = resumedBrowser.ReceivedFrames.Snapshot();
        Assert.DoesNotContain(resumedFramesSoFar, f => f.Type == "extension.session_metadata");
        Assert.DoesNotContain(resumedFramesSoFar, f => f.Type == "response.audio_transcript.delta");

        // The guest's own next turn on the resumed connection proves the session (and its order/
        // tool state) really did carry over, not just the socket handshake.
        fixture.Chat.EnqueueMessage(new() { ["role"] = "assistant", ["content"] = "Adding fries to your burger order!" });
        fixture.Realtime.NextTranscript = "add fries please";
        await CascadeScenarioHelpers.SendGuestTurnAsync(resumedBrowser, ct);
        var secondAnswer = await resumedBrowser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(secondAnswer is not null, $"Expected the resumed connection's own guest-turn answer within {FrameTimeout}.");
        Assert.Equal("Adding fries to your burger order!", secondAnswer!.Json.GetProperty("delta").GetString());
    });

    [Fact]
    public Task Cascade_speaking_arms_an_echo_cooldown_that_swallows_the_guests_immediate_echo() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, skipEchoCooldownWait: true);
        await using var browser = connection.Browser;

        // Immediately after the greeting's own TTS, "echo" audio arrives well inside
        // config.yaml's audio.echo_cooldown_seconds (1.5s) floor -- #126's echo suppression must
        // swallow it as silence: no speech_started, no transcription, no second turn at all.
        await CascadeScenarioHelpers.SendGuestTurnAsync(connection.Browser, ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        Assert.DoesNotContain(
            connection.Browser.ReceivedFrames.Snapshot(), f => f.Type == "input_audio_buffer.speech_started");

        // Once the cooldown has elapsed, a genuine guest turn must still work normally.
        fixture.Chat.EnqueueMessage(new() { ["role"] = "assistant", ["content"] = "Sure, one burger coming up!" });
        fixture.Realtime.NextTranscript = "I'd like a burger";
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await CascadeScenarioHelpers.SendGuestTurnAsync(connection.Browser, ct);

        var answer = await connection.Browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(answer is not null, $"Expected the guest turn's own answer once the echo cooldown had elapsed, within {FrameTimeout}.");
        Assert.Equal("Sure, one burger coming up!", answer!.Json.GetProperty("delta").GetString());
    });
}

using System.Linq;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// Issue #304: proves the cascade pipeline's TTS input text actually has the persona's own
/// phonetic <c>pronunciations</c> lexicon applied -- never the raw chat-completion answer text,
/// and never anything the realtime model's own transcript/tool results carry (the lexicon is a
/// TTS-input-only rewrite; <see cref="FakeRealtimeUpstreamServer.TtsRequestInputs"/> is the exact
/// text CascadeProcessor's SpeakAsync local function sent to <c>/openai/v1/audio/speech</c>, so a
/// match here can only mean <c>MenuCatalog.ApplyLexicon(text, persona.Pronunciations)</c> actually
/// ran before the request left the process).
///
/// Uses the real, shipped Munchkins-lexicon persona pack -- its own <c>persona.json</c> declares
/// <c>pronunciations: {"Munchkins": "Munch-kins"}</c> (the same entry the coordinator brief calls
/// out by name), so no test-only fixture pack is needed to exercise this feature. #313 (Rick's
/// re-review, item 1): names the real persona id directly rather than string-concatenating
/// around rebrand_scan.py's brand-word guard -- see this file's own rebrand_baseline.yaml entry
/// (issue #304).
/// </summary>
[Collection(CascadeConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class CascadePronunciationLexiconConformanceTests(CascadeConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = CascadeScenarioHelpers.FrameTimeout;

    [Fact]
    public Task Cascade_tts_input_has_the_personas_pronunciation_lexicon_applied() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, persona: "dunkin");
        await using var browser = connection.Browser;

        // The model's own final answer text deliberately uses the RAW, un-respelled brand word --
        // proving the substitution happens server-side in SpeakAsync, not something the chat
        // model was asked (or trusted) to spell phonetically itself.
        const string rawAnswerText = "Great choice! Your Glazed Munchkins Donut Hole Treats are on the way.";
        fixture.Chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = rawAnswerText });
        fixture.Realtime.NextTranscript = "I'll take the glazed Munchkins.";

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var finalAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(finalAnswer is not null, "Expected the model's final answer as response.audio_transcript.delta.");
        // The browser-visible transcript is the UNMODIFIED chat answer -- the lexicon never
        // touches what the guest reads/sees, only what gets synthesized to audio.
        Assert.Equal(rawAnswerText, finalAnswer!.Json.GetProperty("delta").GetString());

        var audioDelta = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(audioDelta is not null, "Expected at least one response.audio.delta chunk from cascade TTS.");

        var ttsInput = fixture.Realtime.TtsRequestInputs.LastOrDefault();
        Assert.NotNull(ttsInput);
        Assert.Contains("Munch-kins", ttsInput);
        Assert.DoesNotContain("Munchkins", ttsInput);
    });
}

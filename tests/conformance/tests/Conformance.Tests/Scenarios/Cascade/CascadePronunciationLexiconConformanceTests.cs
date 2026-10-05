using System.Linq;
using System.Text.Json;
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
/// #313 (coordinator fix request, round 4): made brand-neutral -- generic over EVERY shipped pack
/// <see cref="ConformancePersonas.DiscoverFromDisk()"/> finds that actually declares a non-empty
/// <c>pronunciations</c> block, read straight from that pack's own persona.json (never a literal
/// value in this file's own source), so this class names no real persona id and carries no new
/// rebrand_baseline.yaml entry. Asserts at least one shipped pack declares one, so the row can't
/// pass vacuously.
/// </summary>
[Collection(CascadeConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class CascadePronunciationLexiconConformanceTests(CascadeConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = CascadeScenarioHelpers.FrameTimeout;

    /// <summary>Reads every shipped pack's own persona.json (never a cached/typed model) and
    /// returns one (personaId, rawKey, spokenForm) row per declared <c>pronunciations</c> entry,
    /// across every pack -- mirrors <see cref="ConformancePersonas.DiscoverFromDisk()"/>'s own
    /// disk-discovery convention. Plain strings (not a custom record) so xunit's MemberData
    /// serialization needs nothing beyond its built-in support.</summary>
    private static IReadOnlyList<(string PersonaId, string RawKey, string SpokenForm)> DiscoverPronunciationEntries()
    {
        var personasDir = RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot());
        var entries = new List<(string, string, string)>();

        foreach (var personaId in ConformancePersonas.DiscoverFromDisk(personasDir))
        {
            var personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
            using var document = JsonDocument.Parse(File.ReadAllText(personaJsonPath));
            if (!document.RootElement.TryGetProperty("pronunciations", out var pronunciations) ||
                pronunciations.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in pronunciations.EnumerateObject())
            {
                entries.Add((personaId, property.Name, property.Value.GetString() ?? ""));
            }
        }

        return entries;
    }

    public static IEnumerable<object[]> PronunciationEntries() =>
        DiscoverPronunciationEntries().Select(entry => new object[] { entry.PersonaId, entry.RawKey, entry.SpokenForm });

    [Fact]
    public void At_least_one_shipped_pack_declares_a_pronunciation_entry()
    {
        Assert.True(
            DiscoverPronunciationEntries().Count > 0,
            "expected at least one shipped persona pack to declare a pronunciations lexicon " +
            "(otherwise the Theory below passes vacuously)");
    }

    [Theory]
    [MemberData(nameof(PronunciationEntries))]
    public Task Cascade_tts_input_has_the_personas_pronunciation_lexicon_applied(string personaId, string rawKey, string spokenForm) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
            fixture, fixture.Chat, "gpt-5-mini", ct, persona: personaId);
        await using var browser = connection.Browser;

        // The model's own final answer text deliberately uses the RAW, un-respelled key --
        // proving the substitution happens server-side in SpeakAsync, not something the chat
        // model was asked (or trusted) to spell phonetically itself.
        var rawAnswerText = $"Great choice! Your {rawKey} order is on the way.";
        fixture.Chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = rawAnswerText });
        fixture.Realtime.NextTranscript = $"I'll take the {rawKey}.";

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
        Assert.Contains(spokenForm, ttsInput);
        Assert.DoesNotContain(rawKey, ttsInput);
    });
}

using Conformance.Fakes;
using Conformance.Harness;
using System.Text.Json.Nodes;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>A connected cascade browser client past its own connect-time greeting turn, plus the
/// frame sequence number of that greeting's own `extension.round_trip_token` -- see
/// <see cref="CascadeScenarioHelpers.ConnectPastGreetingAsync"/>. Callers should watermark their
/// own guest-turn frame waits against <see cref="GreetingWatermark"/> (e.g.
/// <c>f.Sequence &gt; connection.GreetingWatermark</c>) so a content-agnostic
/// `WaitForAsync(f =&gt; f.Type == "response.audio_transcript.delta")` can never match the
/// greeting's own frames instead of the guest turn's -- <c>Conformance.Fakes.FrameLog.WaitForAsync</c>
/// always scans from the very first recorded frame, not just new arrivals.</summary>
public sealed record CascadeConnection(RealtimeBrowserClient Browser, int GreetingWatermark);

/// <summary>
/// Issue #82: shared connect/turn-simulation plumbing for the cascade conformance rows in this
/// folder. Cascade has no upstream `server_vad` doing turn segmentation for it (that's the whole
/// point of "cascade" -- see `cascade_processor.py`'s `_TurnDetector` docstring), so unlike
/// <c>OrderScenarioHelpers.ConnectAndGreetAsync</c> (which scripts a realtime `response.create`
/// and gets a turn for free), a cascade scenario must synthesize actual PCM16 audio that trips
/// `_TurnDetector`'s own local RMS-energy detector before <c>CascadeProcessor._process_turn</c>
/// ever runs.
/// </summary>
public static class CascadeScenarioHelpers
{
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    // #126: cascade's 300ms acoustic tail after estimated playback (audio.echo_cooldown_seconds
    // capped at 300ms) plus a little scheduling jitter. Replaces the old blanket 1.8s wait.
    public static readonly TimeSpan AcousticTailClearDelay = TimeSpan.FromMilliseconds(400);

    // Mirrors cascade_processor.py's _AUDIO_SAMPLE_RATE (24 kHz mono PCM16, matching the
    // realtime pipeline's own wire format so both pipelines sound identical to a guest).
    private const int SampleRate = 24000;

    // config.yaml's vad.silence_duration_ms default (200ms) translates to
    // int(0.2 * 24000) = 4800 samples of trailing near-silence needed to trip "speech_stopped"
    // (see _TurnDetector.__init__'s own `_silence_samples_needed` calculation). A little over
    // that (not exactly it) avoids relying on the boundary being inclusive vs exclusive.
    private const int SilenceSamplesNeeded = (int)(0.2 * SampleRate) + 200;

    /// <summary>Connects a fresh browser client bound to <paramref name="model"/> (a cascade
    /// catalog id, e.g. "gpt-5-mini") against the persona-defaulted pipeline dispatch -- proving
    /// #82's dispatch seam (`dispatch_processor`/`ProcessorRegistry`) routes it to
    /// <c>CascadeProcessor</c>, never `RTMiddleTier`/realtime, with no `rtmt.py` special-casing.</summary>
    public static Task<RealtimeBrowserClient> ConnectAsync(
        ConformanceFixture fixture, string model, CancellationToken ct, string? persona = null, string? mode = null) =>
        RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, persona: persona, model: model, mode: mode, cancellationToken: ct);

    /// <summary>
    /// Connects, waits for extension.session_metadata, then drains the automatic connect-time
    /// greeting turn (cascade_processor.py's `_start_greeting` fires unconditionally on every
    /// connection, before any guest turn -- see that method's own docstring) so a test's own
    /// `/chat/completions` script and frame-sequence watermark start from a clean, known point.
    /// Enqueues a harmless, fixed greeting reply onto <paramref name="chat"/> BEFORE connecting so
    /// the greeting's own completions call can never dequeue a message a test scripts afterward
    /// for its own guest turn.
    ///
    /// #126: the greeting's fake TTS clip is a few bytes (negligible estimated playback), but
    /// cascade still drops mic audio for a 300ms acoustic tail after playback, so by default this
    /// waits out just that tail (<see cref="AcousticTailClearDelay"/>) -- NOT realtime's 1.5s
    /// cooldown. A row that probes the suppression window itself passes
    /// <paramref name="waitOutAcousticTail"/>: false and scripts its own <c>NextTtsAudio</c>.
    /// </summary>
    public static async Task<CascadeConnection> ConnectPastGreetingAsync(
        ConformanceFixture fixture, FakeChatCompletionsServer chat, string model, CancellationToken ct,
        string? persona = null, string? mode = null, bool waitOutAcousticTail = true)
    {
        chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = "Welcome to the drive-thru!" });
        var browser = await ConnectAsync(fixture, model, ct, persona, mode).ConfigureAwait(false);
        try
        {
            var metadata = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct)
                .ConfigureAwait(false);
            if (metadata is null)
            {
                throw new InvalidOperationException($"Expected extension.session_metadata within {FrameTimeout}.");
            }

            var greetingDone = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "response.done", FrameTimeout, ct)
                .ConfigureAwait(false);
            if (greetingDone is null)
            {
                throw new InvalidOperationException($"Expected the connect-time greeting's own response.done within {FrameTimeout}.");
            }

            // #236 Rick re-review item 4: wait for the greeting's own `extension.round_trip_token`,
            // not just `response.done` -- `response.done` is sent mid-turn-teardown, BEFORE the
            // round-trip token frame (see CascadeProcessor.RunTurnAndSpeakAsync), so a caller that
            // proceeds immediately after `response.done` can race the greeting's own trailing frame
            // and misattribute it to the guest's first turn once GreetingWatermark is used to filter.
            var greetingRoundTrip = await browser.ReceivedFrames.WaitForAsync(
                    f => f.Type == "extension.round_trip_token", FrameTimeout, ct)
                .ConfigureAwait(false);
            if (greetingRoundTrip is null)
            {
                throw new InvalidOperationException(
                    $"Expected the connect-time greeting's own extension.round_trip_token within {FrameTimeout}.");
            }

            if (waitOutAcousticTail)
            {
                await Task.Delay(AcousticTailClearDelay, ct).ConfigureAwait(false);
            }

            return new CascadeConnection(browser, greetingRoundTrip.Sequence);
        }
        catch
        {
            await browser.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Simulates one guest turn end to end: a loud "speech" burst (crosses `_TurnDetector`'s RMS
    /// threshold -- config.yaml `vad.threshold` default 0.5 => cutoff ~16383 on a signed 16-bit
    /// scale) followed by enough trailing near-silence to trip `speech_stopped`. Sent as TWO
    /// separate `input_audio_buffer.append` frames rather than one combined buffer, because
    /// `_TurnDetector.feed` computes a single RMS over whatever bytes arrive in ONE call -- a
    /// single frame mixing loud and silent samples would average out to one ambiguous RMS
    /// instead of a clean speech_started-then-speech_stopped transition.
    /// </summary>
    public static async Task SendGuestTurnAsync(RealtimeBrowserClient browser, CancellationToken ct)
    {
        var speechChunk = GenerateTone(SilenceSamplesNeeded, amplitude: 24000);
        var silenceChunk = GenerateTone(SilenceSamplesNeeded, amplitude: 0);
        await browser.SendInputAudioAppendAsync(Convert.ToBase64String(speechChunk), ct).ConfigureAwait(false);
        await browser.SendInputAudioAppendAsync(Convert.ToBase64String(silenceChunk), ct).ConfigureAwait(false);
    }

    private static byte[] GenerateTone(int sampleCount, short amplitude)
    {
        var bytes = new byte[sampleCount * 2];
        var sample = BitConverter.GetBytes(amplitude);
        for (var i = 0; i < sampleCount; i++)
        {
            sample.CopyTo(bytes, i * 2);
        }
        return bytes;
    }
}

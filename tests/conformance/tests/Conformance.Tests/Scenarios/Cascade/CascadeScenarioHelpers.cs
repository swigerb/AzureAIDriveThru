using Conformance.Harness;

namespace Conformance.Tests.Scenarios.Cascade;

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
        ConformanceFixture fixture, string model, CancellationToken ct, string? persona = null) =>
        RealtimeBrowserClient.ConnectAsync(fixture.Backend!.BaseUri, persona: persona, model: model, cancellationToken: ct);

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

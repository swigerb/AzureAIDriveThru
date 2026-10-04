using Backend.Cascade;
using Backend.Configuration;
using Xunit;

namespace Backend.Tests.Cascade;

/// <summary>Issue #13 Wave 5 (#82): port tests for cascade_processor.py's `_TurnDetector` --
/// local RMS-energy turn segmentation (speech_started/speech_stopped), raw-audio buffering, and
/// `CascadeVadConfig.FromAppConfig`'s config.yaml `vad` section read.</summary>
public sealed class TurnDetectorTests
{
    // threshold=0.5 -> cutoff = 0.5*32767 = 16383.5; sampleRate=1000 and silenceDurationMs=10
    // keep the "samples needed" small (10) so tests stay compact without changing the algorithm.
    private static TurnDetector NewDetector() => new(threshold: 0.5, silenceDurationMs: 10, sampleRate: 1000);

    private static byte[] Pcm16(short sampleValue, int count)
    {
        var bytes = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            bytes[i * 2] = (byte)(sampleValue & 0xFF);
            bytes[i * 2 + 1] = (byte)((sampleValue >> 8) & 0xFF);
        }
        return bytes;
    }

    private static readonly byte[] LoudChunk = Pcm16(20000, count: 4); // RMS 20000 > cutoff 16383.5
    private static readonly byte[] SilentChunk = Pcm16(0, count: 4); // RMS 0 < cutoff

    [Fact]
    public void Feed_ReturnsSpeechStarted_OnlyOnTheFirstLoudChunk()
    {
        var detector = NewDetector();

        Assert.Equal("speech_started", detector.Feed(LoudChunk));
        Assert.True(detector.IsSpeaking);
        Assert.Null(detector.Feed(LoudChunk)); // still speaking -- no repeat event
        Assert.True(detector.IsSpeaking);
    }

    [Fact]
    public void Feed_ReturnsNull_ForSilenceBeforeAnySpeechHasStarted()
    {
        var detector = NewDetector();

        Assert.Null(detector.Feed(SilentChunk));
        Assert.False(detector.IsSpeaking);
    }

    [Fact]
    public void Feed_ReturnsSpeechStopped_OnceTrailingSilenceAccumulatesPastSilenceDuration()
    {
        var detector = NewDetector(); // 10 silent samples needed after speech starts

        Assert.Equal("speech_started", detector.Feed(LoudChunk));
        // 4 silent samples -- below the 10-sample silence requirement, no event yet.
        Assert.Null(detector.Feed(SilentChunk));
        Assert.True(detector.IsSpeaking, "must still be considered speaking while under the silence-duration threshold.");
        // +4 = 8 silent samples -- still below 10.
        Assert.Null(detector.Feed(SilentChunk));
        // +4 = 12 silent samples -- now past the 10-sample requirement.
        Assert.Equal("speech_stopped", detector.Feed(SilentChunk));
    }

    [Fact]
    public void Feed_ResetsSilenceRun_WhenSpeechResumesBeforeSilenceDurationElapses()
    {
        var detector = NewDetector();

        Assert.Equal("speech_started", detector.Feed(LoudChunk));
        Assert.Null(detector.Feed(SilentChunk)); // 4/10 silent samples
        Assert.Null(detector.Feed(LoudChunk)); // speech resumes -- silence run resets to 0, no repeat event
        Assert.True(detector.IsSpeaking);
        // Mutation check: without the silence-run reset on renewed speech, 4 (stale) + 4 + 4 = 12
        // silent samples here would wrongly cross the 10-sample threshold on this very call.
        Assert.Null(detector.Feed(SilentChunk));
        Assert.Null(detector.Feed(SilentChunk));
        Assert.Equal("speech_stopped", detector.Feed(SilentChunk));
    }

    [Fact]
    public void Feed_TreatsATrailingOddByte_AsNotYetAUsableSample()
    {
        var detector = NewDetector();
        var oddChunk = Pcm16(20000, count: 2).Concat(new byte[] { 0x7F }).ToArray(); // 5 bytes: 2 full samples + 1 stray byte

        Assert.Equal("speech_started", detector.Feed(oddChunk));
        // The buffered byte[] must still include the odd trailing byte -- only the RMS
        // computation drops it, not the raw audio kept for transcription.
        Assert.Equal(5, detector.TakeBuffer().Length);
    }

    [Fact]
    public void TakeBuffer_ReturnsAllFedBytesAcrossCallsThenClears()
    {
        var detector = NewDetector();
        detector.Feed(LoudChunk);
        detector.Feed(SilentChunk);

        var buffer = detector.TakeBuffer();
        Assert.Equal(LoudChunk.Concat(SilentChunk).ToArray(), buffer);
        Assert.Empty(detector.TakeBuffer());
    }

    [Fact]
    public void Reset_ClearsSpeakingStateSilenceRunAndBuffer()
    {
        var detector = NewDetector();
        detector.Feed(LoudChunk);
        detector.Feed(SilentChunk);

        detector.Reset();

        Assert.False(detector.IsSpeaking);
        Assert.Empty(detector.TakeBuffer());
        // Fully reset -- a fresh loud chunk fires speech_started again rather than being treated
        // as a continuation of the old (reset) turn.
        Assert.Equal("speech_started", detector.Feed(LoudChunk));
    }

    [Fact]
    public void FromAppConfig_ReadsVadSectionDefaults()
    {
        var vad = CascadeVadConfig.FromAppConfig(AppConfig.Load());

        // config.yaml's own top-level `vad` block, shared with the realtime pipeline's server_vad config.
        Assert.Equal(0.5, vad.Threshold);
        Assert.Equal(200, vad.SilenceDurationMs);
    }
}

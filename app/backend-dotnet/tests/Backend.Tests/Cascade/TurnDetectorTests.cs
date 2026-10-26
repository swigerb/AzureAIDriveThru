using Backend.Cascade;
using Backend.Configuration;

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
        byte[] bytes = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            bytes[i * 2] = (byte)(sampleValue & 0xFF);
            bytes[i * 2 + 1] = (byte)((sampleValue >> 8) & 0xFF);
        }
        return bytes;
    }

    private static readonly byte[] s_loudChunk = Pcm16(20000, count: 4); // RMS 20000 > cutoff 16383.5
    private static readonly byte[] s_silentChunk = Pcm16(0, count: 4); // RMS 0 < cutoff

    [Fact]
    public void Feed_ReturnsSpeechStarted_OnlyOnTheFirstLoudChunk()
    {
        TurnDetector detector = NewDetector();

        Assert.Equal("speech_started", detector.Feed(s_loudChunk));
        Assert.True(detector.IsSpeaking);
        Assert.Null(detector.Feed(s_loudChunk)); // still speaking -- no repeat event
        Assert.True(detector.IsSpeaking);
    }

    [Fact]
    public void Feed_ReturnsNull_ForSilenceBeforeAnySpeechHasStarted()
    {
        TurnDetector detector = NewDetector();

        Assert.Null(detector.Feed(s_silentChunk));
        Assert.False(detector.IsSpeaking);
    }

    [Fact]
    public void Feed_ReturnsSpeechStopped_OnceTrailingSilenceAccumulatesPastSilenceDuration()
    {
        TurnDetector detector = NewDetector(); // 10 silent samples needed after speech starts

        Assert.Equal("speech_started", detector.Feed(s_loudChunk));
        // 4 silent samples -- below the 10-sample silence requirement, no event yet.
        Assert.Null(detector.Feed(s_silentChunk));
        Assert.True(detector.IsSpeaking, "must still be considered speaking while under the silence-duration threshold.");
        // +4 = 8 silent samples -- still below 10.
        Assert.Null(detector.Feed(s_silentChunk));
        // +4 = 12 silent samples -- now past the 10-sample requirement.
        Assert.Equal("speech_stopped", detector.Feed(s_silentChunk));
    }

    [Fact]
    public void Feed_ResetsSilenceRun_WhenSpeechResumesBeforeSilenceDurationElapses()
    {
        TurnDetector detector = NewDetector();

        Assert.Equal("speech_started", detector.Feed(s_loudChunk));
        Assert.Null(detector.Feed(s_silentChunk)); // 4/10 silent samples
        Assert.Null(detector.Feed(s_loudChunk)); // speech resumes -- silence run resets to 0, no repeat event
        Assert.True(detector.IsSpeaking);
        // Mutation check: without the silence-run reset on renewed speech, 4 (stale) + 4 + 4 = 12
        // silent samples here would wrongly cross the 10-sample threshold on this very call.
        Assert.Null(detector.Feed(s_silentChunk));
        Assert.Null(detector.Feed(s_silentChunk));
        Assert.Equal("speech_stopped", detector.Feed(s_silentChunk));
    }

    [Fact]
    public void Feed_TreatsATrailingOddByte_AsNotYetAUsableSample()
    {
        TurnDetector detector = NewDetector();
        byte[] oddChunk = [.. Pcm16(20000, count: 2), .. new byte[] { 0x7F }]; // 5 bytes: 2 full samples + 1 stray byte

        Assert.Equal("speech_started", detector.Feed(oddChunk));
        // The buffered byte[] must still include the odd trailing byte -- only the RMS
        // computation drops it, not the raw audio kept for transcription.
        Assert.Equal(5, detector.TakeBuffer().Length);
    }

    [Fact]
    public void TakeBuffer_ReturnsAllFedBytesAcrossCallsThenClears()
    {
        TurnDetector detector = NewDetector();
        detector.Feed(s_loudChunk);
        detector.Feed(s_silentChunk);

        byte[] buffer = detector.TakeBuffer();
        Assert.Equal(s_loudChunk.Concat(s_silentChunk).ToArray(), buffer);
        Assert.Empty(detector.TakeBuffer());
    }

    [Fact]
    public void Reset_ClearsSpeakingStateSilenceRunAndBuffer()
    {
        TurnDetector detector = NewDetector();
        detector.Feed(s_loudChunk);
        detector.Feed(s_silentChunk);

        detector.Reset();

        Assert.False(detector.IsSpeaking);
        Assert.Empty(detector.TakeBuffer());
        // Fully reset -- a fresh loud chunk fires speech_started again rather than being treated
        // as a continuation of the old (reset) turn.
        Assert.Equal("speech_started", detector.Feed(s_loudChunk));
    }

    [Fact]
    public void FromAppConfig_ReadsCascadeEnergyThreshold_NotRealtimeServerVadThreshold()
    {
        var vad = CascadeVadConfig.FromAppConfig(AppConfig.Load());

        Assert.Equal(0.02, vad.Threshold);
        Assert.Equal(200, vad.SilenceDurationMs);
    }

    [Fact]
    public void ShippedConfig_StartsAndEndsATurnForSpeechAtRealisticMicLevel()
    {
        // Regression: cascade reused realtime's vad.threshold (0.5 -> -6 dBFS RMS), so real speech
        // (about -20 dBFS, RMS ~3277) never started a turn. Low noise (~-50 dBFS) must still not.
        var vad = CascadeVadConfig.FromAppConfig(AppConfig.Load());
        var detector = new TurnDetector(vad.Threshold, silenceDurationMs: 10, sampleRate: 1000);

        Assert.Null(detector.Feed(Pcm16(100, count: 20)));
        Assert.Equal("speech_started", detector.Feed(Pcm16(3277, count: 20)));
        Assert.Equal("speech_stopped", detector.Feed(Pcm16(100, count: 20)));
    }

    [Fact]
    public void ShippedConfig_ShortPauseInsideSilenceWindowDoesNotEndTheTurn()
    {
        var vad = CascadeVadConfig.FromAppConfig(AppConfig.Load());
        var detector = new TurnDetector(vad.Threshold, silenceDurationMs: 10, sampleRate: 1000);

        Assert.Equal("speech_started", detector.Feed(Pcm16(3277, count: 20)));
        Assert.Null(detector.Feed(Pcm16(100, count: 5)));
        Assert.Null(detector.Feed(Pcm16(3277, count: 20)));
        Assert.Null(detector.Feed(Pcm16(100, count: 5)));
        Assert.Equal("speech_stopped", detector.Feed(Pcm16(100, count: 5)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-0.1")]
    [InlineData("loud")]
    public void EnergyThreshold_InvalidValueFailsInsteadOfFallingBack(string raw)
    {
        var section = new Dictionary<object, object> { ["cascade_energy_threshold"] = raw };
        Assert.Throws<InvalidOperationException>(() => CascadeVadConfig.GetEnergyThreshold(section));
    }

    [Fact]
    public void EnergyThreshold_MissingKeyUsesDocumentedDefault() =>
        Assert.Equal(TurnDetector.DefaultEnergyThreshold, CascadeVadConfig.GetEnergyThreshold(new Dictionary<object, object>()));

    // ── #126 echo-suppression cooldown ──────────────────────────────────────────────────────

    [Fact]
    public void Feed_SwallowsLoudAudioAsSilence_WhileNowIsInsideAnArmedEchoCooldown()
    {
        TurnDetector detector = NewDetector();
        detector.StartEchoCooldown(durationSeconds: 1.0, now: 100.0); // cooldown active through t=101.0

        // Still inside the cooldown window (now=100.5 < 101.0) -- loud audio must be swallowed
        // exactly like silence: no speech_started, IsSpeaking stays false.
        Assert.Null(detector.Feed(s_loudChunk, now: 100.5));
        Assert.False(detector.IsSpeaking);
    }

    [Fact]
    public void Feed_DropsSuppressedAudio_SoItNeverReachesTheSttBuffer()
    {
        TurnDetector detector = NewDetector();
        detector.StartEchoCooldown(durationSeconds: 1.0, now: 100.0);

        Assert.Null(detector.Feed(s_loudChunk, now: 100.5));
        Assert.Null(detector.Feed(s_silentChunk, now: 100.9));
        Assert.Empty(detector.TakeBuffer());

        Assert.Equal("speech_started", detector.Feed(s_loudChunk, now: 101.0));
        Assert.Equal(s_loudChunk, detector.TakeBuffer());
    }

    [Fact]
    public void Feed_AcceptsAGuestReplyImmediately_OncePlaybackPlusTailHasElapsed()
    {
        TurnDetector detector = NewDetector();
        detector.StartEchoCooldown(durationSeconds: 2.0 + 0.3, now: 100.0);

        // ~200ms after the 300ms tail ends (#187/#190: short replies are never swallowed).
        Assert.Equal("speech_started", detector.Feed(s_loudChunk, now: 102.5));
    }

    [Fact]
    public void Feed_FiresSpeechStarted_OnceNowHasMovedPastTheEchoCooldownDeadline()
    {
        TurnDetector detector = NewDetector();
        detector.StartEchoCooldown(durationSeconds: 1.0, now: 100.0); // cooldown active through t=101.0

        // Real barge-in: the guest actually talks AFTER the cooldown deadline has passed --
        // must fire speech_started exactly like no cooldown had ever been armed.
        Assert.Equal("speech_started", detector.Feed(s_loudChunk, now: 101.5));
        Assert.True(detector.IsSpeaking);
    }

    [Fact]
    public void StartEchoCooldown_NeverShrinksAnAlreadyArmedLongerDeadline()
    {
        TurnDetector detector = NewDetector();
        detector.StartEchoCooldown(durationSeconds: 5.0, now: 100.0); // deadline = 105.0
        detector.StartEchoCooldown(durationSeconds: 1.0, now: 100.0); // a shorter turn's own arm call -- must NOT shrink the deadline to 101.0

        // Still well inside the ORIGINAL (longer) deadline -- a second, shorter _speak call must
        // never shrink a cooldown another still-in-flight turn already armed.
        Assert.Null(detector.Feed(s_loudChunk, now: 103.0));
        Assert.False(detector.IsSpeaking);
    }

    [Fact]
    public void Feed_NeverAppliesACooldown_WhenNowIsNull()
    {
        TurnDetector detector = NewDetector();
        detector.StartEchoCooldown(durationSeconds: 5.0, now: 100.0);

        // Every pre-#126 production/test caller passes no `now` at all (the default) -- must
        // behave exactly as it always did, cooldown or not.
        Assert.Equal("speech_started", detector.Feed(s_loudChunk));
        Assert.True(detector.IsSpeaking);
    }
}

using Backend.Configuration;

namespace Backend.Cascade;

/// <summary>
/// A minimal, local RMS-energy voice-activity detector (issue #82) -- C# port of
/// cascade_processor.py's `_TurnDetector`. Unlike the realtime pipeline, there is no upstream
/// Realtime API doing `server_vad` turn segmentation for us -- the whole point of "cascade" is
/// that WE own STT/chat/TTS, so we must decide for ourselves when the guest has started and
/// finished speaking. Mirrors config.yaml's existing `vad.threshold`/`vad.silence_duration_ms`
/// (already used to configure the realtime session's own `server_vad`) so both pipelines feel the
/// same to a guest, without pretending to reimplement OpenAI's actual VAD algorithm -- just enough
/// to segment turns for STT.
/// </summary>
public sealed class TurnDetector
{
    private const int AudioSampleRate = 24000;

    private readonly double _cutoff;
    private readonly int _silenceSamplesNeeded;
    private readonly List<byte> _buffer = [];
    private int _silenceRun;

    public TurnDetector(double threshold = 0.5, int silenceDurationMs = 200, int sampleRate = AudioSampleRate)
    {
        _cutoff = threshold * 32767;
        _silenceSamplesNeeded = Math.Max(1, (int)(silenceDurationMs / 1000.0 * sampleRate));
    }

    public bool IsSpeaking { get; private set; }

    public void Reset()
    {
        IsSpeaking = false;
        _silenceRun = 0;
        _buffer.Clear();
    }

    public byte[] TakeBuffer()
    {
        var data = _buffer.ToArray();
        _buffer.Clear();
        return data;
    }

    /// <summary>Feed one chunk of raw PCM16 mono audio. Returns "speech_started" the first time
    /// this turn crosses the energy threshold, "speech_stopped" once enough trailing silence has
    /// elapsed after speech was detected, or null otherwise. Always buffers the raw audio (even
    /// pre-threshold, so a turn's very first word isn't clipped) for the eventual transcription
    /// upload.</summary>
    public string? Feed(byte[] pcm16Bytes)
    {
        _buffer.AddRange(pcm16Bytes);
        var usableLen = pcm16Bytes.Length - (pcm16Bytes.Length % 2);
        if (usableLen <= 0)
        {
            return null;
        }

        var sampleCount = usableLen / 2;
        double sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16Bytes[i * 2] | (pcm16Bytes[i * 2 + 1] << 8));
            sumSquares += (double)sample * sample;
        }
        if (sampleCount == 0)
        {
            return null;
        }
        var rms = Math.Sqrt(sumSquares / sampleCount);

        string? result = null;
        if (rms >= _cutoff)
        {
            if (!IsSpeaking)
            {
                IsSpeaking = true;
                result = "speech_started";
            }
            _silenceRun = 0;
        }
        else if (IsSpeaking)
        {
            _silenceRun += sampleCount;
            if (_silenceRun >= _silenceSamplesNeeded)
            {
                result = "speech_stopped";
            }
        }
        return result;
    }
}

/// <summary>config.yaml's top-level `vad` section (`vad.threshold`/`vad.silence_duration_ms`) --
/// shared verbatim with the realtime pipeline's own `server_vad` config (Realtime/RealtimeSessionConfig.cs),
/// just read independently here since <see cref="TurnDetector"/> is cascade's own local VAD, not
/// a value forwarded in a `session.update` to an upstream Realtime API.</summary>
public sealed record CascadeVadConfig(double Threshold, int SilenceDurationMs)
{
    public static CascadeVadConfig FromAppConfig(AppConfig config)
    {
        var section = config.TryGetSection("vad");
        return new CascadeVadConfig(
            GetDouble(section, "threshold", 0.5),
            GetInt(section, "silence_duration_ms", 200));
    }

    private static double GetDouble(IDictionary<object, object>? section, string key, double fallback)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }
        return value switch
        {
            double d => d,
            int i => i,
            long l => l,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static int GetInt(IDictionary<object, object>? section, string key, int fallback)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }
        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }
}

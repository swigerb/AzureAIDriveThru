using System.Text.Json.Nodes;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rtmt.py's `_to_ga_session` / `_ga_audio_format` / `_strip_output_voice`
/// (docs/persona-architecture.md section 7.4; the issue #13 acceptance criteria's explicitly
/// named "pure function" target). Translates the legacy (2024-10-01-preview) realtime `session`
/// shape the browser speaks into the GA shape the real Azure OpenAI Realtime endpoint requires --
/// GA moved most audio settings under `audio.input`/`audio.output`, renamed a couple of fields,
/// requires a `type` discriminator, and rejects unknown top-level parameters outright instead of
/// ignoring them (unlike the legacy dialect).
/// </summary>
public static class GaSessionTranslator
{
    /// <summary>Session keys the GA realtime API accepts at the top level (rtmt.py's
    /// `_GA_SESSION_TOP_LEVEL`). Anything else a legacy client sends (notably `temperature`,
    /// `disable_audio`) is dropped -- GA has no equivalent field for either.</summary>
    public static readonly IReadOnlySet<string> GaSessionTopLevel = new HashSet<string>
    {
        "type", "model", "instructions", "tools", "tool_choice",
        "max_output_tokens", "output_modalities", "audio", "tracing",
        "include", "prompt", "truncation",
        "reasoning", "parallel_tool_calls",
    };

    /// <summary>Legacy audio formats were bare strings ("pcm16"); GA expects an object.</summary>
    private static JsonObject GaAudioFormat(JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var s))
        {
            return s switch
            {
                "pcm16" => new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                "g711_ulaw" => new JsonObject { ["type"] = "audio/pcmu" },
                "g711_alaw" => new JsonObject { ["type"] = "audio/pcma" },
                _ => new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
            };
        }
        // Already an object (or something else) -- pass through unchanged, same as Python's
        // `_ga_audio_format` returning non-str values as-is.
        return value as JsonObject ?? new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 };
    }

    /// <summary>
    /// Translates a legacy-shaped session object into the GA shape. Never mutates
    /// <paramref name="session"/> -- returns a new <see cref="JsonObject"/>, mirroring Python's
    /// `ga = dict(session)` copy-then-mutate.
    /// </summary>
    public static JsonObject ToGaSession(JsonObject session)
    {
        var ga = (JsonObject)session.DeepClone();
        // Deep-clone the audio sub-objects (rather than reusing ga's own child references) so
        // removing/reassigning "audio"/"input"/"output" below never fights System.Text.Json's
        // single-parent-per-node rule.
        var audio = ga["audio"] is JsonObject existingAudio ? (JsonObject)existingAudio.DeepClone() : new JsonObject();
        var audioIn = audio["input"] is JsonObject existingIn ? (JsonObject)existingIn.DeepClone() : new JsonObject();
        var audioOut = audio["output"] is JsonObject existingOut ? (JsonObject)existingOut.DeepClone() : new JsonObject();
        ga.Remove("audio");

        // input side
        if (Pop(ga, "turn_detection") is { } turnDetection)
        {
            audioIn["turn_detection"] = turnDetection;
        }
        if (Pop(ga, "input_audio_transcription") is { } transcription)
        {
            audioIn["transcription"] = transcription;
        }
        if (Pop(ga, "input_audio_format") is { } inFmt)
        {
            audioIn["format"] = GaAudioFormat(inFmt);
        }
        if (Pop(ga, "input_audio_noise_reduction") is { } noise)
        {
            audioIn["noise_reduction"] = noise;
        }

        // output side
        if (Pop(ga, "voice") is { } voice)
        {
            audioOut["voice"] = voice;
        }
        if (Pop(ga, "output_audio_format") is { } outFmt)
        {
            audioOut["format"] = GaAudioFormat(outFmt);
        }
        if (Pop(ga, "speed") is { } speed)
        {
            audioOut["speed"] = speed;
        }

        // renamed top-level fields
        if (Pop(ga, "max_response_output_tokens") is { } maxTokens)
        {
            ga["max_output_tokens"] = maxTokens;
        }
        if (Pop(ga, "modalities") is { } modalities)
        {
            ga["output_modalities"] = modalities;
        }

        if (audioIn.Count > 0)
        {
            audio["input"] = audioIn;
        }
        if (audioOut.Count > 0)
        {
            audio["output"] = audioOut;
        }
        if (audio.Count > 0)
        {
            ga["audio"] = audio;
        }
        else
        {
            ga.Remove("audio");
        }

        ga["type"] = "realtime";

        foreach (var key in ga.Select(kvp => kvp.Key).Where(k => !GaSessionTopLevel.Contains(k)).ToList())
        {
            ga.Remove(key);
        }

        return ga;
    }

    /// <summary>Removes and returns the node at <paramref name="key"/>, or null if absent/JSON
    /// null -- mirrors Python's `ga.pop(key, None)` walrus-assignment idiom used throughout
    /// `_to_ga_session` (`if (x := ga.pop(...)) is not None:`).</summary>
    private static JsonNode? Pop(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var value) || value is null)
        {
            obj.Remove(key);
            return null;
        }
        obj.Remove(key);
        return value;
    }

    /// <summary>
    /// Removes `audio.output.voice` from a GA session in place. Returns true if a voice was
    /// actually removed. Once assistant audio has appeared on a connection, GA rejects the WHOLE
    /// session.update if `audio.output.voice` differs from the currently-locked voice -- tools,
    /// tool_choice and instructions are lost right along with it -- so a voice-locked update must
    /// never carry one at all.
    /// </summary>
    public static bool StripOutputVoice(JsonObject gaSession)
    {
        if (gaSession["audio"] is not JsonObject audio)
        {
            return false;
        }
        if (audio["output"] is not JsonObject output || !output.ContainsKey("voice"))
        {
            return false;
        }
        output.Remove("voice");
        if (output.Count == 0)
        {
            audio.Remove("output");
        }
        if (audio.Count == 0)
        {
            gaSession.Remove("audio");
        }
        return true;
    }
}

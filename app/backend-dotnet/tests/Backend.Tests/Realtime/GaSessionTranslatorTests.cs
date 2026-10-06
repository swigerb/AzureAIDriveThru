using System.Text.Json.Nodes;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>Byte-for-byte port tests for rtmt.py's `_to_ga_session` / `_ga_audio_format` /
/// `_strip_output_voice` (issue #13's GA translation acceptance target).</summary>
public sealed class GaSessionTranslatorTests
{
    [Fact]
    public void ToGaSession_MovesTurnDetectionAndTranscriptionUnderAudioInput()
    {
        var session = new JsonObject
        {
            ["turn_detection"] = new JsonObject { ["type"] = "server_vad" },
            ["input_audio_transcription"] = new JsonObject { ["model"] = "whisper-1" },
            ["input_audio_noise_reduction"] = new JsonObject { ["type"] = "near_field" },
        };

        var ga = GaSessionTranslator.ToGaSession(session);

        var audioIn = Assert.IsType<JsonObject>(ga["audio"]!["input"]);
        Assert.Equal("server_vad", audioIn["turn_detection"]!["type"]!.GetValue<string>());
        Assert.Equal("whisper-1", audioIn["transcription"]!["model"]!.GetValue<string>());
        Assert.Equal("near_field", audioIn["noise_reduction"]!["type"]!.GetValue<string>());
        Assert.Null(ga["turn_detection"]);
        Assert.Null(ga["input_audio_transcription"]);
        Assert.Null(ga["input_audio_noise_reduction"]);
    }

    [Theory]
    [InlineData("pcm16", "audio/pcm")]
    [InlineData("g711_ulaw", "audio/pcmu")]
    [InlineData("g711_alaw", "audio/pcma")]
    public void ToGaSession_MapsKnownLegacyAudioFormats(string legacy, string ga)
    {
        var session = new JsonObject { ["input_audio_format"] = legacy, ["output_audio_format"] = legacy };

        var result = GaSessionTranslator.ToGaSession(session);

        Assert.Equal(ga, result["audio"]!["input"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal(ga, result["audio"]!["output"]!["format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ToGaSession_UnknownAudioFormatFallsBackToDefaultPcm()
    {
        var session = new JsonObject { ["input_audio_format"] = "some_future_codec" };

        var result = GaSessionTranslator.ToGaSession(session);

        // rtmt.py's `_GA_AUDIO_FORMATS.get(value, {"type": "audio/pcm", "rate": 24000})` -- an
        // unrecognised legacy format string falls back to the pcm16 GA shape rather than being
        // passed through, since GA has no bare-string audio format at all.
        Assert.Equal("audio/pcm", result["audio"]!["input"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal(24000, result["audio"]!["input"]!["format"]!["rate"]!.GetValue<int>());
    }

    [Fact]
    public void ToGaSession_MovesVoiceAndSpeedUnderAudioOutput()
    {
        var session = new JsonObject { ["voice"] = "marin", ["speed"] = 1.1 };

        var ga = GaSessionTranslator.ToGaSession(session);

        Assert.Equal("marin", ga["audio"]!["output"]!["voice"]!.GetValue<string>());
        Assert.Equal(1.1, ga["audio"]!["output"]!["speed"]!.GetValue<double>());
        Assert.Null(ga["voice"]);
        Assert.Null(ga["speed"]);
    }

    [Fact]
    public void ToGaSession_RenamesMaxResponseOutputTokensAndModalities()
    {
        var session = new JsonObject
        {
            ["max_response_output_tokens"] = 512,
            ["modalities"] = new JsonArray("text", "audio"),
        };

        var ga = GaSessionTranslator.ToGaSession(session);

        Assert.Equal(512, ga["max_output_tokens"]!.GetValue<int>());
        Assert.Equal(2, ga["output_modalities"]!.AsArray().Count);
        Assert.Null(ga["max_response_output_tokens"]);
        Assert.Null(ga["modalities"]);
    }

    [Fact]
    public void ToGaSession_AlwaysSetsTypeRealtime()
    {
        var ga = GaSessionTranslator.ToGaSession(new JsonObject { ["type"] = "realtime-legacy" });

        Assert.Equal("realtime", ga["type"]!.GetValue<string>());
    }

    [Fact]
    public void ToGaSession_DropsKeysNotOnTheGaAllowList()
    {
        var session = new JsonObject { ["instructions"] = "hi", ["some_unknown_legacy_field"] = "x" };

        var ga = GaSessionTranslator.ToGaSession(session);

        Assert.Equal("hi", ga["instructions"]!.GetValue<string>());
        Assert.Null(ga["some_unknown_legacy_field"]);
    }

    [Fact]
    public void ToGaSession_EmptyAudioObjectIsOmittedEntirely()
    {
        var ga = GaSessionTranslator.ToGaSession(new JsonObject { ["instructions"] = "hi" });

        Assert.Null(ga["audio"]);
    }

    [Fact]
    public void ToGaSession_DoesNotMutateTheInputObject()
    {
        var session = new JsonObject { ["voice"] = "marin" };

        GaSessionTranslator.ToGaSession(session);

        Assert.Equal("marin", session["voice"]!.GetValue<string>());
    }

    [Fact]
    public void StripOutputVoice_RemovesVoiceAndReturnsTrueWhenPresent()
    {
        var ga = new JsonObject { ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = "marin" } } };

        var stripped = GaSessionTranslator.StripOutputVoice(ga);

        Assert.True(stripped);
        // Voice was the only field, so `output` (and then `audio`) are cleaned up too -- asserted
        // separately in the dedicated cleanup test below.
        Assert.Null(ga["audio"]);
    }

    [Fact]
    public void StripOutputVoice_CleansUpNowEmptyOutputAndAudioObjects()
    {
        var ga = new JsonObject { ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = "marin" } } };

        GaSessionTranslator.StripOutputVoice(ga);

        Assert.Null(ga["audio"]);
    }

    [Fact]
    public void StripOutputVoice_PreservesOtherOutputFieldsWhenPresent()
    {
        var ga = new JsonObject
        {
            ["audio"] = new JsonObject
            {
                ["output"] = new JsonObject { ["voice"] = "marin", ["speed"] = 1.0 },
            },
        };

        GaSessionTranslator.StripOutputVoice(ga);

        Assert.NotNull(ga["audio"]);
        Assert.Equal(1.0, ga["audio"]!["output"]!["speed"]!.GetValue<double>());
    }

    [Fact]
    public void StripOutputVoice_ReturnsFalseWhenNoVoicePresent()
    {
        Assert.False(GaSessionTranslator.StripOutputVoice(new JsonObject()));
    }
}

using System.Text.Json.Nodes;

namespace Backend.Realtime;

/// <summary>
/// Port of the server-owned configuration fields on app/backend/rtmt.py's `RTMiddleTier`
/// (`system_message`, `temperature`, `max_tokens`, `disable_audio`, `voice_choice`,
/// `transcription_model`, `reasoning_effort`, `parallel_tool_calls`, `reasoning_model`,
/// `_reasoning_rejected`, `tools`) -- everything <see cref="RealtimeSessionBuilder"/> overlays
/// onto a session object. One instance per deployment (not per session/persona): a session's own
/// PERSONA-specific overrides (voice, system message, tool schemas, reasoning override) are passed
/// explicitly to each builder call instead, exactly like `_build_session`'s own
/// voice/system_message/reasoning_override parameters.
/// </summary>
public sealed class RealtimeSessionConfig
{
    public string? Deployment { get; init; }
    /// <summary>The persona's system prompt, used whenever a builder call's own `systemMessage`
    /// parameter is <see cref="Overridable.Unset{T}"/>.</summary>
    public string? SystemMessage { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public bool? DisableAudio { get; init; }
    public string? VoiceChoice { get; init; }
    public string? TranscriptionModel { get; init; }
    public string? ReasoningEffort { get; init; }
    public bool? ParallelToolCalls { get; init; }

    /// <summary>The `model.reasoning_model` operator switch: true/false forces it, null is "auto"
    /// (deployment-name heuristic / catalog override). Mutable -- rtmt.py exposes this as a plain
    /// instance attribute that tests flip at runtime (e.g. `rtmt.reasoning_model = True`).</summary>
    public bool? ReasoningModel { get; set; }

    /// <summary>Flipped once the deployment rejects `reasoning` at runtime despite the name
    /// check, so later sessions on this same process stop sending it. Mutable by design --
    /// mirrors rtmt.py's `self._reasoning_rejected = True` runtime flip.</summary>
    public bool ReasoningRejected { get; set; }

    /// <summary>Whether reasoning-model-only fields (`reasoning`, `parallel_tool_calls`) may be
    /// sent upstream at all -- see <see cref="ReasoningRules"/>'s class doc for the exact
    /// precedence order this implements.</summary>
    public bool IsReasoningModel(Overridable<bool?> reasoningOverride)
    {
        if (ReasoningRejected)
        {
            return false;
        }
        if (ReasoningModel.HasValue)
        {
            return ReasoningModel.Value;
        }
        if (reasoningOverride.HasValue)
        {
            return reasoningOverride.Value ?? false;
        }
        return ReasoningRules.DeploymentSupportsReasoning(Deployment);
    }

    /// <summary>Whether `reasoning` will actually be sent upstream (also requires a non-omitted
    /// effort level).</summary>
    public bool ReasoningEnabled(Overridable<bool?> reasoningOverride) =>
        ReasoningRules.NormalizeReasoningEffort(ReasoningEffort) is not null && IsReasoningModel(reasoningOverride);
}

/// <summary>
/// Port of app/backend/rtmt.py's `_build_session` / `build_bootstrap_session_update` /
/// `build_fallback_session_update` (docs/persona-architecture.md section 7.4). Overlays the
/// server-owned configuration (system prompt, temperature, voice, tools, reasoning) onto a
/// legacy-shaped session object and translates it to the GA shape via
/// <see cref="GaSessionTranslator"/>.
/// </summary>
public static class RealtimeSessionBuilder
{
    /// <summary>What the browser's useRealtime.startSession() sends. The middle tier applies the
    /// same values itself the moment the upstream socket opens, so a socket the browser never
    /// configures behaves exactly like one it did.</summary>
    public static JsonObject BootstrapClientSession() => new()
    {
        ["turn_detection"] = new JsonObject
        {
            ["type"] = "server_vad",
            ["threshold"] = 0.7,
            ["prefix_padding_ms"] = 300,
            ["silence_duration_ms"] = 500,
        },
        ["input_audio_transcription"] = new JsonObject { ["model"] = "whisper-1" },
    };

    /// <summary>The fallback carries only what the conversation cannot work without. No voice
    /// (cannot_update_voice), no audio config, no reasoning -- the usual suspects when GA rejects
    /// an update.</summary>
    private static readonly string[] FallbackSessionKeys = ["type", "instructions", "tools", "tool_choice"];

    /// <summary>
    /// Overlays <paramref name="config"/>'s server-owned settings and <paramref name="toolSchemas"/>
    /// onto <paramref name="session"/>, then translates the result to the GA shape.
    ///
    /// `voiceLocked` must be true once the upstream conversation contains assistant audio -- from
    /// then on GA rejects any session.update whose voice differs from the current one with
    /// `cannot_update_voice`, and it rejects the WHOLE event (tools/tool_choice/instructions lost
    /// right along with it).
    /// </summary>
    public static JsonObject BuildSession(
        RealtimeSessionConfig config,
        JsonObject session,
        IReadOnlyList<JsonObject> toolSchemas,
        bool voiceLocked = false,
        Overridable<string?> voice = default,
        Overridable<string?> systemMessage = default,
        Overridable<bool?> reasoningOverride = default)
    {
        session = (JsonObject)session.DeepClone();

        var effectiveSystemMessage = systemMessage.HasValue ? systemMessage.Value : config.SystemMessage;
        if (effectiveSystemMessage is not null)
        {
            session["instructions"] = effectiveSystemMessage;
        }
        if (config.Temperature is not null)
        {
            session["temperature"] = config.Temperature;
        }
        if (config.MaxTokens is not null)
        {
            session["max_response_output_tokens"] = config.MaxTokens;
        }
        if (config.DisableAudio is not null)
        {
            session["disable_audio"] = config.DisableAudio;
        }

        var effectiveVoice = voice.HasValue ? voice.Value : config.VoiceChoice;
        if (effectiveVoice is not null)
        {
            session["voice"] = effectiveVoice;
        }

        session["tool_choice"] = toolSchemas.Count > 0 ? "auto" : "none";
        var tools = new JsonArray();
        foreach (var schema in toolSchemas)
        {
            tools.Add(schema.DeepClone());
        }
        session["tools"] = tools;

        // Server-owned: never trust a client-supplied value for the transcription model, since
        // an unsupported one takes the tools down with it.
        if (!string.IsNullOrEmpty(config.TranscriptionModel))
        {
            session["input_audio_transcription"] = new JsonObject { ["model"] = config.TranscriptionModel };
        }
        else
        {
            session.Remove("input_audio_transcription");
        }

        // Server-owned: never trust a client-supplied value for these, since an unsupported one
        // takes the tools down with it.
        session.Remove("reasoning");
        session.Remove("parallel_tool_calls");
        if (config.IsReasoningModel(reasoningOverride))
        {
            var effort = ReasoningRules.NormalizeReasoningEffort(config.ReasoningEffort);
            if (effort is not null)
            {
                session["reasoning"] = new JsonObject { ["effort"] = effort };
            }
            if (config.ParallelToolCalls is not null)
            {
                session["parallel_tool_calls"] = config.ParallelToolCalls;
            }
        }

        var gaSession = GaSessionTranslator.ToGaSession(session);
        if (voiceLocked)
        {
            GaSessionTranslator.StripOutputVoice(gaSession);
        }
        return gaSession;
    }

    /// <summary>
    /// Serialises the session.update the middle tier sends as the very first frame on every
    /// upstream socket, before any browser traffic is relayed. Without it the upstream session
    /// runs on the service defaults (no tools, generic instructions, server VAD auto-responding)
    /// until the browser's own session.update arrives -- and if the model speaks in that window
    /// the voice locks and every later session.update carrying our voice is rejected, so tools are
    /// never registered for that conversation.
    /// </summary>
    public static JsonObject BuildBootstrapSessionUpdate(
        RealtimeSessionConfig config,
        IReadOnlyList<JsonObject> toolSchemas,
        string? eventId = null,
        Overridable<string?> voice = default,
        Overridable<string?> systemMessage = default,
        Overridable<bool?> reasoningOverride = default)
    {
        var session = BuildSession(config, BootstrapClientSession(), toolSchemas,
            voice: voice, systemMessage: systemMessage, reasoningOverride: reasoningOverride);
        return new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = eventId ?? EventIds.NewEventId("sonic_bootstrap"),
            ["session"] = session,
        };
    }

    /// <summary>
    /// Serialises the minimal session.update sent when GA rejects one of ours. Only `type`,
    /// `instructions`, `tools` and `tool_choice` -- whatever field got the original rejected, the
    /// persona keeps its tools and its own voice/system prompt.
    /// </summary>
    public static JsonObject BuildFallbackSessionUpdate(
        RealtimeSessionConfig config,
        IReadOnlyList<JsonObject> toolSchemas,
        string? eventId = null,
        Overridable<string?> voice = default,
        Overridable<string?> systemMessage = default,
        Overridable<bool?> reasoningOverride = default)
    {
        var full = BuildSession(config, [], toolSchemas, voiceLocked: true,
            voice: voice, systemMessage: systemMessage, reasoningOverride: reasoningOverride);
        var session = new JsonObject();
        foreach (var key in FallbackSessionKeys)
        {
            if (full[key] is { } value)
            {
                session[key] = value.DeepClone();
            }
        }
        return new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = eventId ?? EventIds.NewEventId("sonic_fallback"),
            ["session"] = session,
        };
    }
}

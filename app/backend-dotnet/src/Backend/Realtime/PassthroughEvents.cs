namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/audio_pipeline.py's `_PASSTHROUGH_SERVER_TYPES` / `_GA_TO_LEGACY_EVENTS`:
/// high-frequency upstream→browser event types that never need middle-tier modification, plus the
/// GA→legacy event name translation the frontend (which still expects the 2024-10-01-preview
/// names) needs for six of them. The C# port always fully parses each frame rather than
/// fast-pathing on a raw substring match first (issue #13's documented fast-path-skip
/// simplification -- correctness over the micro-perf win), so these are consulted only after the
/// frame's own `type` has already been read.
/// </summary>
public static class PassthroughEvents
{
    public static readonly IReadOnlySet<string> ServerTypes = new HashSet<string>
    {
        // GA event names (gpt-realtime-2.1 via /openai/v1/realtime)
        "response.output_audio.delta",
        "response.output_audio.done",
        "response.output_audio_transcript.delta",
        "response.output_audio_transcript.done",
        "response.output_text.delta",
        "response.output_text.done",
        // Legacy event names (2024-10-01-preview via /openai/realtime)
        "response.audio.delta",
        "response.audio.done",
        "response.audio_transcript.delta",
        "response.audio_transcript.done",
        "response.text.delta",
        "response.text.done",
        // Unchanged across versions
        "response.content_part.added",
        "response.content_part.done",
        "input_audio_buffer.speech_started",
        "input_audio_buffer.speech_stopped",
        "input_audio_buffer.committed",
        "rate_limits.updated",
    };

    /// <summary>GA → legacy event name translation for client compatibility -- the frontend
    /// expects the legacy names; the GA /openai/v1 endpoint sends these instead.</summary>
    public static readonly IReadOnlyDictionary<string, string> GaToLegacy = new Dictionary<string, string>
    {
        ["response.output_audio.delta"] = "response.audio.delta",
        ["response.output_audio.done"] = "response.audio.done",
        ["response.output_audio_transcript.delta"] = "response.audio_transcript.delta",
        ["response.output_audio_transcript.done"] = "response.audio_transcript.done",
        ["response.output_text.delta"] = "response.text.delta",
        ["response.output_text.done"] = "response.text.done",
    };
}

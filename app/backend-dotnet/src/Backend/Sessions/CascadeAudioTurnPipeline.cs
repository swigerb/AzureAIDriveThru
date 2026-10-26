using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Models;
using Backend.Personas;

namespace Backend.Sessions;

/// <summary>
/// Transcribes guest audio, streams synthesized speech, and closes failed turns.
/// Writes use the connection token so turn cancellation cannot abort the WebSocket.
/// </summary>
internal sealed class CascadeAudioTurnPipeline(
    WebSocket browserSocket,
    Persona persona,
    CascadeProcessor.CascadeSessionState state,
    TurnDetector detector,
    ModelCatalog catalog,
    FoundryAudioClient audioClient,
    CascadeRateLimitSettings rateLimitSettings,
    double echoCooldownSeconds,
    Func<string> getSessionId,
    TimeProvider timeProvider,
    Func<JsonObject, CancellationToken, Task> notifyClientAsync,
    Func<double> nowSeconds,
    ILogger logger,
    CancellationToken ct)
{
    private string SessionId => getSessionId();
    public async Task<string> TranscribeAsync(byte[] turnAudio, CancellationToken turnCt)
    {
        CascadeAudioConfig cascadeAudio = catalog.CascadeAudio
            ?? throw new InvalidOperationException("config.yaml has no models.cascade transcription/tts configured.");
        string deployment = catalog.DeploymentFor(cascadeAudio.Transcription)
            ?? throw new InvalidOperationException(
                $"Cascade transcription model '{cascadeAudio.Transcription}' has no AZURE_AI_MODEL_DEPLOYMENTS entry.");
        return await CascadeRateLimit.WithRetryAsync(
            rateLimitSettings,
            () => audioClient.TranscribeAsync(turnAudio, deployment, CascadeProcessor.AudioSampleRate, turnCt),
            "transcription", notifyClientAsync, SessionId, logger, turnCt, timeProvider).ConfigureAwait(false);
    }

    public async Task SpeakAsync(string text, string responseId, CancellationToken turnCt)
    {
        CascadeAudioConfig cascadeAudio = catalog.CascadeAudio
            ?? throw new InvalidOperationException("config.yaml has no models.cascade transcription/tts configured.");
        string deployment = catalog.DeploymentFor(cascadeAudio.Tts)
            ?? throw new InvalidOperationException(
                $"Cascade TTS model '{cascadeAudio.Tts}' has no AZURE_AI_MODEL_DEPLOYMENTS entry.");
        // Issue #304: apply this persona's own phonetic pronunciation lexicon ONLY to the TTS
        // input text -- never to the chat transcript/history sent to the browser (that still
        // carries the unmodified `text`). Mirrors cascade_processor.py's
        // `_apply_pronunciations`/`_speak`.
        string ttsText = persona.Pronunciations is { Count: > 0 } pronunciations
            ? MenuCatalog.ApplyLexicon(text, pronunciations)
            : text;
        await CascadeRateLimit.WithRetryAsync(
            rateLimitSettings,
            async () =>
            {
                byte[] pcm = await audioClient.SpeakAsync(ttsText, state.Voice, deployment, turnCt).ConfigureAwait(false);
                if (echoCooldownSeconds > 0)
                {
                    // #126: arm echo suppression for the GUEST'S estimated speaker playback
                    // of this reply (not this loop's own fast send time) plus a short acoustic
                    // tail (`_echoCooldownSeconds`, capped at 300ms in the constructor).
                    // 0 disables suppression entirely, identically to cascade_processor.py's
                    // `_speak`. PCM16 mono => 2 bytes/sample.
                    double durationSeconds = pcm.Length / (double)(CascadeProcessor.AudioSampleRate * 2);
                    detector.StartEchoCooldown(durationSeconds + echoCooldownSeconds, nowSeconds());
                }
                for (int offset = 0; offset < pcm.Length; offset += CascadeProcessor.TtsChunkBytes)
                {
                    int chunkLength = Math.Min(CascadeProcessor.TtsChunkBytes, pcm.Length - offset);
                    byte[] chunk = pcm.AsSpan(offset, chunkLength).ToArray();
                    await CascadeProcessor.SendTextAsync(browserSocket, new JsonObject
                    {
                        ["type"] = "response.audio.delta",
                        ["response_id"] = responseId,
                        ["delta"] = Convert.ToBase64String(chunk),
                    }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
                }
            },
            "text-to-speech", notifyClientAsync, SessionId, logger, turnCt, timeProvider).ConfigureAwait(false);
    }

    public async Task SendFailedResponseDoneAsync(string responseId, string message, CancellationToken turnCt)
    {
        // #262: closes out a turn that failed (non-429) after `response.created` was already
        // sent, so the browser is never left thinking a response is still in progress. Shape
        // mirrors the Realtime API's own failed-response `response.done` (`status: "failed"`,
        // `status_details.error`) -- see RealtimeProcessor's own passthrough of upstream's
        // `response.done`, which never needs to construct this shape itself -- so the
        // frontend's shared `onReceivedResponseDone` handler needs no cascade-specific
        // branch, just a `status` check. The plain `error` event mirrors the Realtime API's
        // own `error` passthrough (upstream protocol errors reach the browser the same way).
        await CascadeProcessor.SendTextAsync(browserSocket, new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "server_error", ["message"] = message },
        }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
        await CascadeProcessor.SendTextAsync(browserSocket, new JsonObject
        {
            ["type"] = "response.done",
            ["response"] = new JsonObject
            {
                ["id"] = responseId,
                ["status"] = "failed",
                ["status_details"] = new JsonObject
                {
                    ["type"] = "failed",
                    ["error"] = new JsonObject { ["type"] = "server_error", ["message"] = message },
                },
                ["output"] = new JsonArray(),
            },
        }.ToJsonString(), turnCt, ct).ConfigureAwait(false);
    }
}

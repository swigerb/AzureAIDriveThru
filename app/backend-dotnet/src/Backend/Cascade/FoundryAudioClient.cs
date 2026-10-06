using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Backend.Realtime;

namespace Backend.Cascade;

/// <summary>
/// Issue #82: a plain <see cref="HttpClient"/> REST client for the two Foundry/Azure OpenAI audio
/// endpoints cascade_processor.py's `_transcribe`/`_speak` call directly (no SDK -- these are
/// simple, stable REST contracts, same rationale as <see cref="FoundryChatClient"/>'s own doc
/// comment). Both endpoints live on the SAME account/base URI the realtime pipeline already
/// authenticates against (`AZURE_OPENAI_EASTUS2_ENDPOINT`) -- see
/// <see cref="Conformance.Fakes.FakeRealtimeUpstreamServer.ExpectedCascadeBearerToken"/>'s own doc
/// comment for why. Bearer-authenticated (never `api-key`) with the same
/// <see cref="FoundryChatClient.CognitiveServicesScope"/> cascade's chat calls use.
/// </summary>
public sealed class FoundryAudioClient(HttpClient httpClient, string endpoint, IUpstreamBearerTokenProvider credential)
{
    private readonly string _endpoint = endpoint.TrimEnd('/');

    /// <summary>Azure OpenAI's preview `/openai/v1/audio/transcriptions` route returns 404
    /// DeploymentNotFound for gpt-4o-transcribe deployments (verified live 2026-10-05) even though
    /// `/openai/v1/audio/speech` works, so STT uses the deployment-scoped route. Mirrors
    /// cascade_processor.py's <c>transcription_url</c>.</summary>
    internal const string TranscriptionApiVersion = "2024-10-21"; // GA; verified live with gpt-4o-transcribe 2026-10-05

    internal static string TranscriptionUrl(string endpoint, string deployment) =>
        $"{endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/audio/transcriptions?api-version={TranscriptionApiVersion}";

    /// <summary>Uploads <paramref name="pcm16Bytes"/> (wrapped in a minimal WAV container, see
    /// <see cref="WavEncoder"/>) to the deployment-scoped transcription route (see <see cref="TranscriptionUrl"/>) and returns the transcript
    /// text (empty string if the response has none). Port of cascade_processor.py's `_transcribe`.</summary>
    public async Task<string> TranscribeAsync(byte[] pcm16Bytes, string deployment, int sampleRate, CancellationToken ct)
    {
        var wavBytes = WavEncoder.PcmToWav(pcm16Bytes, sampleRate);

        using var content = new MultipartFormDataContent
        {
            { new StringContent(deployment), "model" },
        };
        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "turn.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionUrl(_endpoint, deployment))
        {
            Content = content,
        };
        var token = await credential.GetTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        var responseText = await ReadBodySafelyAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw BuildHttpException(response, responseText);
        }

        var root = JsonNode.Parse(responseText) as JsonObject;
        return root?["text"]?.GetValue<string>() ?? "";
    }

    /// <summary>Posts <paramref name="text"/> to `/openai/v1/audio/speech` and returns the raw
    /// PCM16 response bytes (`response_format: "pcm"`, same as cascade_processor.py's own
    /// `_speak` request). Chunking the result into outbound `response.audio.delta` frames is the
    /// caller's job (<see cref="Sessions.CascadeProcessor"/>), not this client's -- this is a pure
    /// HTTP concern.</summary>
    public async Task<byte[]> SpeakAsync(string text, string voice, string deployment, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = deployment,
            ["input"] = text,
            ["voice"] = voice,
            ["response_format"] = "pcm",
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/openai/v1/audio/speech")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        var token = await credential.GetTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await ReadBodySafelyAsync(response, ct).ConfigureAwait(false);
            throw BuildHttpException(response, errorText);
        }
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static FoundryHttpException BuildHttpException(HttpResponseMessage response, string body)
    {
        double? retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
        var message = string.IsNullOrEmpty(body) ? $"HTTP {(int)response.StatusCode}" : body;
        return new FoundryHttpException((int)response.StatusCode, retryAfter, message);
    }
}

/// <summary>Wraps raw PCM16 mono audio in a minimal 44-byte WAV (RIFF) container -- C# has no
/// stdlib equivalent of Python's `wave` module (which cascade_processor.py's own
/// `_pcm16_to_wav_bytes` uses), so this writes the RIFF header by hand. Produces a standard
/// uncompressed PCM WAV: `RIFF` chunk descriptor, `fmt ` subchunk (1 channel, 16-bit, the given
/// sample rate), `data` subchunk containing the PCM bytes verbatim.</summary>
public static class WavEncoder
{
    public static byte[] PcmToWav(byte[] pcm16Bytes, int sampleRate, int channels = 1, int bitsPerSample = 16)
    {
        var blockAlign = channels * (bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        var dataSize = pcm16Bytes.Length;
        var riffSize = 36 + dataSize;

        using var stream = new MemoryStream(44 + dataSize);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(riffSize);
            writer.Write("WAVE"u8);

            writer.Write("fmt "u8);
            writer.Write(16); // PCM fmt chunk size
            writer.Write((short)1); // PCM audio format
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);

            writer.Write("data"u8);
            writer.Write(dataSize);
            writer.Write(pcm16Bytes);
        }
        return stream.ToArray();
    }
}

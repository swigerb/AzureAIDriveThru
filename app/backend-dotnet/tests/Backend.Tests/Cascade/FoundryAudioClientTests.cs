using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Backend.Cascade;
using Backend.Realtime;
using Xunit;

namespace Backend.Tests.Cascade;

/// <summary>Issue #13 Wave 5 (#82): tests for <see cref="FoundryAudioClient"/> (`/audio/transcriptions`
/// + `/audio/speech` REST clients) and <see cref="WavEncoder"/>'s raw-PCM16 -> RIFF/WAV framing.</summary>
public sealed class FoundryAudioClientTests
{
    private const string Endpoint = "https://fake-foundry.example.com/";

    private static FoundryAudioClient NewClient(QueuedFoundryHttpHandler handler, string token = "fake-token") =>
        new(new HttpClient(handler), Endpoint, new StaticBearerTokenProvider(token));

    [Fact]
    public async Task TranscribeAsync_PostsMultipartWavToTranscriptionsEndpointAndReturnsText()
    {
        var handler = new QueuedFoundryHttpHandler().Enqueue(HttpStatusCode.OK, """{"text":"two burgers please"}""");
        var client = NewClient(handler, token: "my-token");
        var pcm = new byte[] { 0x01, 0x00, 0x02, 0x00 };

        var transcript = await client.TranscribeAsync(pcm, deployment: "gpt-4o-transcribe", sampleRate: 24000, CancellationToken.None);

        Assert.Equal("two burgers please", transcript);
        var request = Assert.Single(handler.Requests);
        // Endpoint trailing slash is trimmed, same as FoundryChatClient.
        Assert.Equal("https://fake-foundry.example.com/openai/deployments/gpt-4o-transcribe/audio/transcriptions?api-version=2025-03-01-preview", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("my-token", request.Headers.Authorization!.Parameter);
        Assert.IsType<MultipartFormDataContent>(request.Content);
    }

    [Fact]
    public async Task TranscribeAsync_MissingTextField_ReturnsEmptyStringRatherThanThrowing()
    {
        var handler = new QueuedFoundryHttpHandler().Enqueue(HttpStatusCode.OK, "{}");
        var client = NewClient(handler);

        var transcript = await client.TranscribeAsync([0x00, 0x00], "gpt-4o-transcribe", 24000, CancellationToken.None);

        Assert.Equal("", transcript);
    }

    [Fact]
    public async Task TranscribeAsync_NonSuccessStatus_ThrowsFoundryHttpException()
    {
        var handler = new QueuedFoundryHttpHandler().Enqueue(HttpStatusCode.ServiceUnavailable, """{"error":"upstream down"}""");
        var client = NewClient(handler);

        var exc = await Assert.ThrowsAsync<FoundryHttpException>(() =>
            client.TranscribeAsync([0x00, 0x00], "gpt-4o-transcribe", 24000, CancellationToken.None));

        Assert.Equal(503, exc.StatusCode);
        Assert.Contains("upstream down", exc.Message);
    }

    [Fact]
    public async Task SpeakAsync_PostsJsonBodyWithPcmResponseFormatAndReturnsRawBytes()
    {
        var pcmResponse = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new QueuedFoundryHttpHandler().EnqueueBytes(HttpStatusCode.OK, pcmResponse);
        var client = NewClient(handler, token: "my-token");

        var result = await client.SpeakAsync("Welcome to Test Co.!", voice: "marin", deployment: "gpt-4o-mini-tts", CancellationToken.None);

        Assert.Equal(pcmResponse, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://fake-foundry.example.com/openai/v1/audio/speech", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);

        var body = System.Text.Json.Nodes.JsonNode.Parse(handler.RequestBodies.Single())!.AsObject();
        Assert.Equal("gpt-4o-mini-tts", body["model"]!.GetValue<string>());
        Assert.Equal("Welcome to Test Co.!", body["input"]!.GetValue<string>());
        Assert.Equal("marin", body["voice"]!.GetValue<string>());
        Assert.Equal("pcm", body["response_format"]!.GetValue<string>());
    }

    [Fact]
    public async Task SpeakAsync_NonSuccessStatus_ThrowsFoundryHttpExceptionWithRetryAfterHeader()
    {
        var handler = new QueuedFoundryHttpHandler().Enqueue(
            HttpStatusCode.TooManyRequests, """{"error":"rate limited"}""", retryAfterHeader: "3");
        var client = NewClient(handler);

        var exc = await Assert.ThrowsAsync<FoundryHttpException>(() =>
            client.SpeakAsync("hi", "marin", "gpt-4o-mini-tts", CancellationToken.None));

        Assert.Equal(429, exc.StatusCode);
        Assert.Equal(3.0, exc.RetryAfterSeconds);
    }

    [Fact]
    public void PcmToWav_ProducesACorrectFortyFourByteRiffHeaderForMonoSixteenBit()
    {
        var pcm = new byte[] { 0x11, 0x22, 0x33, 0x44 }; // 2 fake samples
        var wav = WavEncoder.PcmToWav(pcm, sampleRate: 24000);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + pcm.Length, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16)); // fmt chunk size
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 20)); // PCM format
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 22)); // mono
        Assert.Equal(24000, BitConverter.ToInt32(wav, 24)); // sample rate
        Assert.Equal(24000 * 2, BitConverter.ToInt32(wav, 28)); // byte rate = sampleRate * blockAlign(2)
        Assert.Equal((short)2, BitConverter.ToInt16(wav, 32)); // block align
        Assert.Equal((short)16, BitConverter.ToInt16(wav, 34)); // bits per sample
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
        Assert.Equal(pcm, wav[44..]);
    }

    [Fact]
    public void PcmToWav_EmptyPcm_ProducesAnEmptyDataChunk()
    {
        var wav = WavEncoder.PcmToWav([], sampleRate: 24000);

        Assert.Equal(44, wav.Length);
        Assert.Equal(0, BitConverter.ToInt32(wav, 40));
    }
}

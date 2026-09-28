using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Conformance.Fakes;

/// <summary>
/// A Kestrel-hosted fake of the companion local-runtime process (issue #81, design doc section
/// 7.4), the HTTP/JSON contract `app/backend/local_runtime.py`'s `HttpLocalRuntimeClient` speaks:
///
///   POST /v1/transcribe  raw 16 kHz PCM16 in, <c>{"text": ...}</c> out (<see cref="NextTranscript"/>)
///   POST /v1/chat        <c>{"messages": [...], "tools": [...]}</c> in, <c>{"content", "tool_calls"}</c> out
///   POST /v1/speak       <c>{"text", "voice"}</c> in, raw 24 kHz PCM16 out
///
/// Scripting and synchronisation mirror <see cref="FakeChatCompletionsServer"/> (#118): a FIFO of
/// scripted chat responses, a TaskCompletionSource-swap arrival signal instead of polling, and a
/// <see cref="HoldNextChatResponse"/> gate whose held request flips
/// <see cref="LocalChatRequest.Aborted"/> when the backend drops the connection first. That flag
/// is the positive proof a barge-in cancelled the in-flight chat call.
/// </summary>
public sealed class FakeLocalRuntimeServer : IAsyncDisposable
{
    /// <summary>~0.1 s of 24 kHz mono PCM16 silence: enough for the backend to emit at least one
    /// <c>response.audio.delta</c> frame without bloating every test's frame log.</summary>
    private const int SpeakResponseBytes = 4800;

    private static readonly JsonObject DefaultChatResponse = new() { ["content"] = "All set." };

    private WebApplication? _app;
    private readonly Lock _gate = new();
    private readonly Queue<JsonObject> _scriptedChat = new();
    private readonly List<LocalChatRequest> _chatRequests = [];
    private readonly List<int> _transcribeBodyLengths = [];
    private readonly List<LocalSpeakRequest> _speakRequests = [];
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _chatSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _pendingChatGate;

    public FakeLocalRuntimeServer(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");

    /// <summary>Text returned by the next <c>/v1/transcribe</c> call (and every later one until
    /// changed), same convention as <see cref="FakeRealtimeUpstreamServer.NextTranscript"/>.</summary>
    public string NextTranscript { get; set; } = "";

    public IReadOnlyList<LocalChatRequest> ChatRequests
    {
        get { lock (_gate) { return [.. _chatRequests]; } }
    }

    public int ChatRequestCount
    {
        get { lock (_gate) { return _chatRequests.Count; } }
    }

    /// <summary>Body length in bytes of every <c>/v1/transcribe</c> request, in arrival order.</summary>
    public IReadOnlyList<int> TranscribeBodyLengths
    {
        get { lock (_gate) { return [.. _transcribeBodyLengths]; } }
    }

    public IReadOnlyList<LocalSpeakRequest> SpeakRequests
    {
        get { lock (_gate) { return [.. _speakRequests]; } }
    }

    /// <summary>Scripts a final answer (no tool calls) for the next <c>/v1/chat</c> call.</summary>
    public void EnqueueFinal(string content) => Enqueue(new JsonObject { ["content"] = content });

    /// <summary>Scripts one tool call (flat <c>{id, name, arguments}</c>, the local wire shape, not
    /// the Chat Completions <c>function</c> nesting) for the next <c>/v1/chat</c> call.</summary>
    public void EnqueueToolCall(string id, string name, string argumentsJson) => Enqueue(new JsonObject
    {
        ["content"] = null,
        ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = id, ["name"] = name, ["arguments"] = argumentsJson }),
    });

    private void Enqueue(JsonObject response)
    {
        lock (_gate)
        {
            _scriptedChat.Enqueue(response);
        }
    }

    /// <summary>Holds the NEXT <c>/v1/chat</c> response until <see cref="ResponseGate.Release"/> or
    /// until the client aborts the request (see <see cref="FakeChatCompletionsServer.HoldNextResponse"/>).</summary>
    public ResponseGate HoldNextChatResponse()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingChatGate = tcs;
        }
        return new ResponseGate(tcs);
    }

    /// <summary>Awaits until at least <paramref name="count"/> <c>/v1/chat</c> requests have
    /// arrived, reacting to each arrival; <c>false</c> on timeout.</summary>
    public async Task<bool> WaitForChatRequestCountAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            Task signalTask;
            lock (_gate)
            {
                if (_chatRequests.Count >= count)
                {
                    return true;
                }
                signalTask = _chatSignal.Task;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var delayTask = Task.Delay(remaining, _timeProvider, cancellationToken);
            if (await Task.WhenAny(signalTask, delayTask).ConfigureAwait(false) != signalTask)
            {
                return false;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/", () => Results.Ok());
        app.MapPost("/v1/transcribe", HandleTranscribeAsync);
        app.MapPost("/v1/chat", HandleChatAsync);
        app.MapPost("/v1/speak", HandleSpeakAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        BaseUri = new Uri(app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleTranscribeAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
        string transcript;
        lock (_gate)
        {
            _transcribeBodyLengths.Add((int)buffer.Length);
            transcript = NextTranscript;
        }
        await context.Response.WriteAsJsonAsync(new { text = transcript }, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleChatAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        var recorded = new LocalChatRequest(document.RootElement.Clone());

        TaskCompletionSource released;
        TaskCompletionSource? gate;
        JsonObject response;
        lock (_gate)
        {
            _chatRequests.Add(recorded);
            released = _chatSignal;
            _chatSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate = _pendingChatGate;
            _pendingChatGate = null;
            response = _scriptedChat.TryDequeue(out var next)
                ? next
                : (JsonObject)DefaultChatResponse.DeepClone();
        }
        released.TrySetResult();

        if (gate is not null)
        {
            try
            {
                await gate.Task.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                recorded.MarkAborted();
                return;
            }
        }

        context.Response.ContentType = "application/json";
        try
        {
            await context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            recorded.MarkAborted();
        }
    }

    private async Task HandleSpeakAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        var root = document.RootElement;
        var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
        var voice = root.TryGetProperty("voice", out var voiceProp) ? voiceProp.GetString() ?? "" : "";
        lock (_gate)
        {
            _speakRequests.Add(new LocalSpeakRequest(text, voice));
        }

        context.Response.ContentType = "application/octet-stream";
        await context.Response.Body.WriteAsync(new byte[SpeakResponseBytes], context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>One recorded <c>/v1/chat</c> request. Mutable only for <see cref="Aborted"/>, for the
/// same reason as <see cref="ChatCompletionsRequest"/>.</summary>
public sealed class LocalChatRequest(JsonElement rawBody)
{
    public JsonElement RawBody { get; } = rawBody;

    /// <summary>True once the backend dropped this request's connection while its response was
    /// held on <see cref="FakeLocalRuntimeServer.HoldNextChatResponse"/>'s gate.</summary>
    public bool Aborted { get; private set; }

    internal void MarkAborted() => Aborted = true;
}

public sealed record LocalSpeakRequest(string Text, string Voice);

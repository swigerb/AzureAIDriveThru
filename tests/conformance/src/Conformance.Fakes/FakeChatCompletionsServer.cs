using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Conformance.Fakes;

/// <summary>
/// A Kestrel-hosted fake of the Azure AI Foundry Model Inference chat-completions endpoint
/// (`POST {endpoint}/chat/completions`), issue #82's cascade pipeline. `CascadeProcessor`
/// (app/backend/cascade_processor.py) posts one request per tool-calling round via
/// `azure.ai.inference.aio.ChatCompletionsClient.complete(messages=, model=, tools=)`, which
/// serialises to a plain `{"messages": [...], "model": "<deployment>", "tools": [...]}` JSON
/// body against `/chat/completions?api-version=...`, authenticated with a bearer token from
/// `DefaultAzureCredential` (real deployments) or the conformance harness's own fake credential
/// (see `conformance_hooks.py`'s `cascade_credential()` / `CONFORMANCE_CASCADE_FAKE_TOKEN`).
///
/// Mirrors <see cref="RealtimeScript"/>'s own scripted-response convention (a FIFO queue a test
/// enqueues from) but deliberately simplified to plain request/response scripting: unlike the
/// realtime WS relay, Chat Completions has no persistent per-connection state to track -- every
/// call is a fresh, independent HTTP round trip.
///
/// Issue #118 Rick re-review item 2: request tracking and the response-hold gate below both use
/// the same TaskCompletionSource-swap "signal" idiom as <see cref="FrameLog"/>/
/// <see cref="ConnectionRegistry"/> -- a waiter reacts to the next arrival immediately instead of
/// polling on a fixed interval, and nothing here calls <c>Task.Delay</c> to simulate "still
/// working" (that was the barge-in row's own flake source: a fixed <c>ResponseDelay</c> plus a
/// polling loop with its own margin, racing real wall-clock time against a shared, possibly
/// loaded machine).
/// </summary>
public sealed class FakeChatCompletionsServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly Queue<ScriptedResponse> _scriptedResponses = new();
    private readonly List<ChatCompletionsRequest> _requests = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _requestSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set by <see cref="HoldNextResponse"/>, consumed by the very next request to
    /// arrive (never more than one at a time -- a scenario that needs this must send exactly one
    /// held turn before releasing/aborting it or scripting another).</summary>
    private TaskCompletionSource? _pendingResponseGate;

    private static readonly JsonObject DefaultMessage = new()
    {
        ["role"] = "assistant",
        ["content"] = "All set.",
    };

    public FakeChatCompletionsServer(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");

    /// <summary>
    /// Bearer token every request must present (checked verbatim as `Bearer {token}` -- no real
    /// JWT validation, the same fidelity level <see cref="FakeRealtimeUpstreamServer"/>'s own
    /// api-key check already uses). <c>null</c> (the default) disables the check entirely.
    /// </summary>
    public string? ExpectedBearerToken { get; set; }

    /// <summary>Every request this server has received so far, in arrival order.</summary>
    public IReadOnlyList<ChatCompletionsRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Total requests ever received -- usable as a watermark exactly like
    /// <see cref="ConnectionRegistry.TotalAcceptedCount"/>.</summary>
    public int RequestCount
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    /// <summary>
    /// Enqueues the `message` object (OpenAI/Foundry chat-completions shape, e.g.
    /// <c>{"role":"assistant","content":"..."}</c> for a final answer, or
    /// <c>{"role":"assistant","content":null,"tool_calls":[{"id":"...","type":"function",
    /// "function":{"name":"...","arguments":"...json string..."}}]}</c> for a tool-calling round)
    /// returned for the NEXT `/chat/completions` call. FIFO across multiple calls, shared with
    /// <see cref="EnqueueErrorStatus"/> -- one scripted response consumed per request. Once the
    /// queue is empty, every further request gets <see cref="DefaultMessage"/> (a harmless,
    /// tool-call-free canned reply) rather than an error, so a scenario that only cares about
    /// the first N rounds doesn't need to script every single one all the way to the model's own
    /// round cap.
    /// </summary>
    public void EnqueueMessage(JsonObject message)
    {
        lock (_gate)
        {
            _scriptedResponses.Enqueue(new ScriptedResponse(message.DeepClone().AsObject(), StatusCode: null, RetryAfterSeconds: null));
        }
    }

    /// <summary>
    /// Enqueues an HTTP error status (e.g. 429, matching Rick's #118 review item 5's "a 429 from
    /// chat/STT/TTS goes through the same rate-limit notice path as realtime") for the NEXT
    /// `/chat/completions` call, in the same FIFO as <see cref="EnqueueMessage"/>. When
    /// <paramref name="retryAfterSeconds"/> is given, a `Retry-After` response header is set too
    /// -- mirrors `_retry_hint_of` (cascade_processor.py)'s own hint-extraction path.
    /// </summary>
    public void EnqueueErrorStatus(int statusCode, int? retryAfterSeconds = null)
    {
        lock (_gate)
        {
            _scriptedResponses.Enqueue(new ScriptedResponse(Message: null, statusCode, retryAfterSeconds));
        }
    }

    /// <summary>
    /// Issue #118 Rick re-review item 2: replaces the old fixed <c>ResponseDelay</c> with an
    /// event-driven gate. Holds the NEXT request's response (whatever <see cref="EnqueueMessage"/>
    /// scripted for it) until the returned <see cref="ResponseGate"/>'s
    /// <see cref="ResponseGate.Release"/> is called. While held, the request handler is suspended
    /// on the gate task racing <c>context.RequestAborted</c> -- if the client (the cascade
    /// processor's own HTTP call) aborts the connection first, e.g. because
    /// `_cancel_current_turn` cancelled the awaiting task, the corresponding
    /// <see cref="ChatCompletionsRequest.Aborted"/> flips <c>true</c> and the response is never
    /// written at all. That is the positive, in-process proof a barge-in genuinely cancelled the
    /// in-flight call -- not a race against how long a scripted delay happened to be.
    /// </summary>
    public ResponseGate HoldNextResponse()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingResponseGate = tcs;
        }
        return new ResponseGate(tcs);
    }

    /// <summary>
    /// Awaits until at least <paramref name="count"/> requests have been received, reacting
    /// immediately to each new arrival (no fixed-interval polling -- same idiom as
    /// <see cref="FrameLog.WaitForAsync"/>/<see cref="ConnectionRegistry.WaitForNextAsync"/>).
    /// Returns <c>true</c> once satisfied, <c>false</c> on timeout so callers can assert with a
    /// clear message instead of this throwing.
    /// </summary>
    public async Task<bool> WaitForRequestCountAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            Task signalTask;
            lock (_gate)
            {
                if (_requests.Count >= count)
                {
                    return true;
                }
                signalTask = _requestSignal.Task;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var delayTask = Task.Delay(remaining, _timeProvider, cancellationToken);
            var completed = await Task.WhenAny(signalTask, delayTask).ConfigureAwait(false);
            if (completed != signalTask)
            {
                return false;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default, int? fixedPort = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{fixedPort?.ToString() ?? "0"}");
        var app = builder.Build();
        app.MapGet("/", () => Results.Ok());
        app.MapPost("/chat/completions", HandleCompletionAsync);

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

    private async Task HandleCompletionAsync(HttpContext context)
    {
        if (ExpectedBearerToken is not null)
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (!string.Equals(header, $"Bearer {ExpectedBearerToken}", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        var root = document.RootElement;
        var model = root.TryGetProperty("model", out var modelProp) ? modelProp.GetString() ?? "" : "";
        var messageCount = root.TryGetProperty("messages", out var messagesProp)
            && messagesProp.ValueKind == JsonValueKind.Array
            ? messagesProp.GetArrayLength() : 0;
        var hasTools = root.TryGetProperty("tools", out var toolsProp)
            && toolsProp.ValueKind == JsonValueKind.Array && toolsProp.GetArrayLength() > 0;

        var recorded = new ChatCompletionsRequest(model, messageCount, hasTools, root.Clone());
        TaskCompletionSource? gate;
        ScriptedResponse scripted;
        TaskCompletionSource released;
        lock (_gate)
        {
            _requests.Add(recorded);
            released = _requestSignal;
            _requestSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            gate = _pendingResponseGate;
            _pendingResponseGate = null;

            scripted = _scriptedResponses.TryDequeue(out var next) ? next : new ScriptedResponse((JsonObject)DefaultMessage.DeepClone(), StatusCode: null, RetryAfterSeconds: null);
        }
        released.TrySetResult();

        if (gate is not null)
        {
            // Issue #118 Rick re-review item 2: suspend here instead of a fixed Task.Delay --
            // released either by the test calling ResponseGate.Release() (the turn completes
            // normally) or by the client aborting the connection first (barge-in cancelled the
            // awaiting call), whichever happens first.
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

        if (scripted.StatusCode is int statusCode)
        {
            if (scripted.RetryAfterSeconds is int retryAfterSeconds)
            {
                context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            }
            context.Response.StatusCode = statusCode;
            return;
        }

        var message = scripted.Message!;
        var responseBody = new JsonObject
        {
            ["id"] = "fake-cascade-completion",
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = message.ContainsKey("tool_calls") ? "tool_calls" : "stop",
                ["message"] = message,
            }),
        };

        context.Response.ContentType = "application/json";
        try
        {
            await context.Response.WriteAsync(responseBody.ToJsonString(), context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            recorded.MarkAborted();
        }
    }
}

/// <summary>
/// One recorded `/chat/completions` request, captured for test assertions -- e.g. "the deployment
/// name in `model` matches this cascade model's own `AZURE_AI_MODEL_DEPLOYMENTS` entry" or "tools
/// were present on the very first round" (proving tool definitions reach the model at all).
/// Mutable (unlike the rest of this file's request/response shapes) only for
/// <see cref="Aborted"/>: issue #118 Rick re-review item 2 needs a way to flip that flag on
/// this SAME already-recorded instance once the client aborts the connection while its response
/// was held on <see cref="FakeChatCompletionsServer.HoldNextResponse"/>'s gate -- a plain
/// immutable record would force a separate abort-tracking collection a test would then have to
/// correlate back to the right request by hand.
/// </summary>
public sealed class ChatCompletionsRequest(string model, int messageCount, bool hasTools, JsonElement rawBody)
{
    public string Model { get; } = model;
    public int MessageCount { get; } = messageCount;
    public bool HasTools { get; } = hasTools;
    public JsonElement RawBody { get; } = rawBody;

    /// <summary>True once the client (the cascade processor's own HTTP call) aborted this
    /// request's connection before its held response was released -- the positive proof a
    /// barge-in's cancellation actually reached the in-flight chat-completions call, not merely
    /// that a later turn's answer happened to arrive.</summary>
    public bool Aborted { get; private set; }

    internal void MarkAborted() => Aborted = true;
}

/// <summary>
/// One entry in <see cref="FakeChatCompletionsServer"/>'s shared FIFO -- either a scripted
/// success `message` (<see cref="StatusCode"/> null) or a scripted error status (e.g. 429),
/// optionally carrying a `Retry-After` hint. Never both.
/// </summary>
public sealed record ScriptedResponse(JsonObject? Message, int? StatusCode, int? RetryAfterSeconds);

/// <summary>
/// Handle returned by <see cref="FakeChatCompletionsServer.HoldNextResponse"/>. <see cref="Release"/>
/// lets the held request's response proceed; safe to call more than once (e.g. a test releasing
/// defensively after already asserting the request was aborted -- the underlying
/// <see cref="TaskCompletionSource"/> only ever transitions once).
/// </summary>
public sealed class ResponseGate(TaskCompletionSource source)
{
    public void Release() => source.TrySetResult();
}


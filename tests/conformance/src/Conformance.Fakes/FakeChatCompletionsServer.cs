using System.Collections.Concurrent;
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
/// </summary>
public sealed class FakeChatCompletionsServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly ConcurrentQueue<ScriptedResponse> _scriptedResponses = new();
    private readonly ConcurrentQueue<ChatCompletionsRequest> _requests = new();

    private static readonly JsonObject DefaultMessage = new()
    {
        ["role"] = "assistant",
        ["content"] = "All set.",
    };

    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0");

    /// <summary>
    /// Bearer token every request must present (checked verbatim as `Bearer {token}` -- no real
    /// JWT validation, the same fidelity level <see cref="FakeRealtimeUpstreamServer"/>'s own
    /// api-key check already uses). <c>null</c> (the default) disables the check entirely.
    /// </summary>
    public string? ExpectedBearerToken { get; set; }

    /// <summary>Held before writing each response body -- lets a barge-in conformance scenario
    /// script a slow-enough model turn that a second guest utterance can arrive and cancel it
    /// mid-flight (see <c>Cascade_barge_in_cancels_the_in_flight_turn_before_it_speaks</c>).
    /// Zero (the default) means no delay.</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Every request this server has received so far, in arrival order.</summary>
    public IReadOnlyList<ChatCompletionsRequest> Requests => _requests.ToArray();

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
    public void EnqueueMessage(JsonObject message) =>
        _scriptedResponses.Enqueue(new ScriptedResponse(message.DeepClone().AsObject(), StatusCode: null, RetryAfterSeconds: null));

    /// <summary>
    /// Enqueues an HTTP error status (e.g. 429, matching Rick's #118 review item 5's "a 429 from
    /// chat/STT/TTS goes through the same rate-limit notice path as realtime") for the NEXT
    /// `/chat/completions` call, in the same FIFO as <see cref="EnqueueMessage"/>. When
    /// <paramref name="retryAfterSeconds"/> is given, a `Retry-After` response header is set too
    /// -- mirrors `_retry_hint_of` (cascade_processor.py)'s own hint-extraction path.
    /// </summary>
    public void EnqueueErrorStatus(int statusCode, int? retryAfterSeconds = null) =>
        _scriptedResponses.Enqueue(new ScriptedResponse(Message: null, statusCode, retryAfterSeconds));

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

        using var document = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
        var root = document.RootElement;
        var model = root.TryGetProperty("model", out var modelProp) ? modelProp.GetString() ?? "" : "";
        var messageCount = root.TryGetProperty("messages", out var messagesProp)
            && messagesProp.ValueKind == JsonValueKind.Array
            ? messagesProp.GetArrayLength() : 0;
        var hasTools = root.TryGetProperty("tools", out var toolsProp)
            && toolsProp.ValueKind == JsonValueKind.Array && toolsProp.GetArrayLength() > 0;
        _requests.Enqueue(new ChatCompletionsRequest(model, messageCount, hasTools, root.Clone()));

        var scripted = _scriptedResponses.TryDequeue(out var next) ? next : new ScriptedResponse((JsonObject)DefaultMessage.DeepClone(), StatusCode: null, RetryAfterSeconds: null);
        if (ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(ResponseDelay, context.RequestAborted).ConfigureAwait(false);
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
        await context.Response.WriteAsync(responseBody.ToJsonString()).ConfigureAwait(false);
    }
}

/// <summary>
/// One recorded `/chat/completions` request, captured for test assertions -- e.g. "the deployment
/// name in `model` matches this cascade model's own `AZURE_AI_MODEL_DEPLOYMENTS` entry" or "tools
/// were present on the very first round" (proving tool definitions reach the model at all).
/// </summary>
public sealed record ChatCompletionsRequest(string Model, int MessageCount, bool HasTools, JsonElement RawBody);

/// <summary>
/// One entry in <see cref="FakeChatCompletionsServer"/>'s shared FIFO -- either a scripted
/// success `message` (<see cref="StatusCode"/> null) or a scripted error status (e.g. 429),
/// optionally carrying a `Retry-After` hint. Never both.
/// </summary>
public sealed record ScriptedResponse(JsonObject? Message, int? StatusCode, int? RetryAfterSeconds);

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Realtime;

namespace Backend.Cascade;

/// <summary>
/// Issue #82: a plain <see cref="HttpClient"/> REST client for the Azure AI Foundry Model
/// Inference chat-completions endpoint (`POST {endpoint}/chat/completions`) -- the C# stand-in for
/// cascade_processor.py's `azure.ai.inference.aio.ChatCompletionsClient`. Deliberately NOT a
/// dependency on the azure-ai-inference SDK (no C# equivalent is wired into this repo, and the
/// wire shape is a simple, stable, plain-JSON REST contract -- see the conformance harness's
/// <c>FakeChatCompletionsServer</c>'s own doc comment) -- every message
/// is represented as a raw <see cref="JsonObject"/> throughout (built by <see cref="CascadeChatMessage"/>
/// below, or threaded straight from a previous response's own `choices[0].message` for an
/// assistant tool-call turn), matching the rest of this codebase's own convention of working with
/// <see cref="JsonObject"/>/<see cref="JsonNode"/> directly rather than introducing a parallel set
/// of typed message DTOs (see <see cref="Sessions.RealtimeProcessor"/>'s own frame-building style).
/// </summary>
internal sealed class FoundryChatClient(HttpClient httpClient, string endpoint, IUpstreamBearerTokenProvider credential)
{
    /// <summary>Port of cascade_processor.py's `_COGNITIVE_SERVICES_SCOPE` -- the same bearer
    /// scope the realtime pipeline's own upstream connect uses (Realtime/UpstreamAuth.cs).</summary>
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>Rick's #236 review item 1 (HIGH, blocking): azure-ai-inference 1.0.0b9's
    /// `ChatCompletionsClient` always sends this as the `api-version` query parameter (see
    /// `_configuration.py`'s `api_version: str = kwargs.pop("api_version", ..., "2024-05-01-preview")`,
    /// applied to every operation including `/chat/completions`) -- never overridden anywhere in
    /// cascade_processor.py, so this is the real, deployed contract. Without it, the real Foundry
    /// `/models` endpoint rejects every request, so every real cascade turn would have failed.</summary>
    public const string ApiVersion = "2024-05-01-preview";

    private readonly string _endpoint = endpoint.TrimEnd('/');

    /// <summary>Posts ONE `/chat/completions` request and returns the raw `choices[0].message`
    /// object exactly as the service returned it (see class doc for why this stays a
    /// <see cref="JsonObject"/> rather than a typed DTO). <paramref name="toolDefinitions"/> is
    /// omitted from the request body entirely when empty/null, mirroring
    /// cascade_processor.py's own `tools=tool_defs or None`. Throws
    /// <see cref="FoundryHttpException"/> for a non-2xx response (its `StatusCode` is what
    /// <see cref="CascadeRateLimit.WithRetryAsync{T}"/> inspects for a 429).</summary>
    public async Task<JsonObject> CompleteAsync(
        IReadOnlyList<JsonObject> messages,
        string deployment,
        IReadOnlyList<JsonObject>? toolDefinitions,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["model"] = deployment,
        };
        if (toolDefinitions is { Count: > 0 })
        {
            body["tools"] = new JsonArray(toolDefinitions.Select(t => (JsonNode)t.DeepClone()).ToArray());
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/chat/completions?api-version={ApiVersion}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
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

        var root = JsonNode.Parse(responseText) as JsonObject
            ?? throw new InvalidOperationException("Foundry chat-completions response was not a JSON object.");
        var choices = root["choices"] as JsonArray;
        var firstChoice = choices?.Count > 0 ? choices[0] as JsonObject : null;
        var message = firstChoice?["message"] as JsonObject
            ?? throw new InvalidOperationException("Foundry chat-completions response had no choices[0].message.");
        return message;
    }

    private static async Task<string> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
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

/// <summary>Builders for the plain chat-completions message shape (mirrors
/// cascade_processor.py's `azure.ai.inference.models.{SystemMessage,UserMessage,AssistantMessage,
/// ToolMessage}` -- same four roles, same fields, just built as raw JSON here instead of typed SDK
/// objects).</summary>
internal static class CascadeChatMessage
{
    public static JsonObject System(string content) => new() { ["role"] = "system", ["content"] = content };

    public static JsonObject User(string content) => new() { ["role"] = "user", ["content"] = content };

    public static JsonObject Assistant(string? content) => new() { ["role"] = "assistant", ["content"] = content };

    public static JsonObject Tool(string content, string toolCallId) =>
        new() { ["role"] = "tool", ["content"] = content, ["tool_call_id"] = toolCallId };
}

/// <summary>
/// Converts tools.py's flat Realtime-API-style schemas (`{"type": "function", "name": ...,
/// "parameters": ...}`) into the nested Chat-Completions-style tool definition shape
/// (`{"type": "function", "function": {"name", "description", "parameters"}}`) the Foundry
/// chat-completions endpoint expects -- C# port of cascade_processor.py's `_tool_definitions`.
/// </summary>
internal static class CascadeToolDefinitions
{
    public static IReadOnlyList<JsonObject> FromToolSchemas(IReadOnlyList<JsonObject> toolSchemas)
    {
        var definitions = new List<JsonObject>(toolSchemas.Count);
        foreach (var schema in toolSchemas)
        {
            var name = schema["name"]?.GetValue<string>() ?? "";
            var description = schema["description"]?.GetValue<string>() ?? "";
            var parameters = schema["parameters"] is JsonNode p ? p.DeepClone() : new JsonObject();
            definitions.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = parameters,
                },
            });
        }
        return definitions;
    }
}

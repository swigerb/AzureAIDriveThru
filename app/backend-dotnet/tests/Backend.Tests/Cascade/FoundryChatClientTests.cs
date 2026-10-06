using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Realtime;

namespace Backend.Tests.Cascade;

/// <summary>Queued fake <see cref="HttpMessageHandler"/> for the Cascade REST clients -- same
/// shape as <c>Search\SearchToolTests.cs</c>'s own <c>QueuedHttpHandler</c>, duplicated here
/// (rather than shared) since it's a tiny, narrowly-scoped fake and the two test areas have no
/// other coupling.</summary>
internal sealed class QueuedFoundryHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];

    public QueuedFoundryHttpHandler Enqueue(HttpStatusCode status, string body, string? retryAfterHeader = null)
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfterHeader is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfterHeader);
            }
            return response;
        });
        return this;
    }

    public QueuedFoundryHttpHandler EnqueueBytes(HttpStatusCode status, byte[] body)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            if (request.Content is MultipartFormDataContent)
            {
                RequestBodies.Add("<multipart>");
            }
            else
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
        }
        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":"no queued response"}""", Encoding.UTF8, "application/json"),
            };
        }
        return _responses.Dequeue()(request);
    }
}

/// <summary>Issue #13 Wave 5 (#82): tests for <see cref="FoundryChatClient"/> (the
/// `/chat/completions` REST client), <see cref="CascadeChatMessage"/>'s four role builders, and
/// <see cref="CascadeToolDefinitions.FromToolSchemas"/>'s flat-Realtime-schema -> nested-Chat-
/// Completions-schema conversion.</summary>
public sealed class FoundryChatClientTests
{
    private const string Endpoint = "https://fake-foundry.example.com";

    private static FoundryChatClient NewClient(QueuedFoundryHttpHandler handler, string token = "fake-token") =>
        new(new HttpClient(handler), Endpoint, new StaticBearerTokenProvider(token));

    [Fact]
    public async Task CompleteAsync_PostsToChatCompletionsWithBearerAuthAndReturnsFirstChoiceMessage()
    {
        var handler = new QueuedFoundryHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"choices":[{"message":{"role":"assistant","content":"Hi there!"}}]}
            """);
        var client = NewClient(handler, token: "my-token");

        var message = await client.CompleteAsync(
            [CascadeChatMessage.System("be nice"), CascadeChatMessage.User("hello")],
            deployment: "gpt-5-mini", toolDefinitions: null, CancellationToken.None);

        Assert.Equal("assistant", message["role"]!.GetValue<string>());
        Assert.Equal("Hi there!", message["content"]!.GetValue<string>());

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Endpoint}/chat/completions?api-version=2024-05-01-preview", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("my-token", request.Headers.Authorization!.Parameter);

        var body = JsonNode.Parse(handler.RequestBodies.Single())!.AsObject();
        Assert.Equal("gpt-5-mini", body["model"]!.GetValue<string>());
        Assert.Equal(2, body["messages"]!.AsArray().Count);
        Assert.False(body.ContainsKey("tools"), "tools must be omitted entirely when no tool definitions are given.");
    }

    [Fact]
    public async Task CompleteAsync_IncludesToolsOnlyWhenNonEmpty()
    {
        var handler = new QueuedFoundryHttpHandler().Enqueue(HttpStatusCode.OK, """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"search","arguments":"{}"}}]}}]}
            """);
        var client = NewClient(handler);
        var toolDefs = CascadeToolDefinitions.FromToolSchemas([
            new JsonObject { ["name"] = "search", ["description"] = "search the menu", ["parameters"] = new JsonObject { ["type"] = "object" } },
        ]);

        var message = await client.CompleteAsync([CascadeChatMessage.User("do a search")], "gpt-5-mini", toolDefs, CancellationToken.None);

        Assert.Null(message["content"]);
        Assert.Equal("call_1", message["tool_calls"]![0]!["id"]!.GetValue<string>());
        var body = JsonNode.Parse(handler.RequestBodies.Single())!.AsObject();
        var tools = body["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal("search", tools[0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task CompleteAsync_NonSuccessStatus_ThrowsFoundryHttpExceptionWithStatusAndBody()
    {
        var handler = new QueuedFoundryHttpHandler().Enqueue(
            HttpStatusCode.TooManyRequests, """{"error":{"message":"Please try again in 2s"}}""");
        var client = NewClient(handler);

        var exc = await Assert.ThrowsAsync<FoundryHttpException>(() =>
            client.CompleteAsync([CascadeChatMessage.User("hi")], "gpt-5-mini", null, CancellationToken.None));

        Assert.Equal(429, exc.StatusCode);
        Assert.Contains("try again in 2s", exc.Message);
    }

    [Fact]
    public async Task CompleteAsync_PrefersRetryAfterHeaderOverFreeTextHint()
    {
        var handler = new QueuedFoundryHttpHandler().Enqueue(
            HttpStatusCode.TooManyRequests, """{"error":{"message":"try again in 2s"}}""", retryAfterHeader: "5");
        var client = NewClient(handler);

        var exc = await Assert.ThrowsAsync<FoundryHttpException>(() =>
            client.CompleteAsync([CascadeChatMessage.User("hi")], "gpt-5-mini", null, CancellationToken.None));

        Assert.Equal(5.0, exc.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CompleteAsync_NoRetryAfterHeaderAndNoHint_LeavesRetryAfterSecondsNull(string? body)
    {
        var handler = new QueuedFoundryHttpHandler().Enqueue(HttpStatusCode.TooManyRequests, body ?? "");
        var client = NewClient(handler);

        var exc = await Assert.ThrowsAsync<FoundryHttpException>(() =>
            client.CompleteAsync([CascadeChatMessage.User("hi")], "gpt-5-mini", null, CancellationToken.None));

        Assert.Null(exc.RetryAfterSeconds);
    }

    [Fact]
    public void CascadeChatMessage_BuildersProduceTheFourExpectedRoleShapes()
    {
        Assert.Equal("system", CascadeChatMessage.System("sys")["role"]!.GetValue<string>());
        Assert.Equal("sys", CascadeChatMessage.System("sys")["content"]!.GetValue<string>());

        Assert.Equal("user", CascadeChatMessage.User("hi")["role"]!.GetValue<string>());

        var assistantNull = CascadeChatMessage.Assistant(null);
        Assert.Equal("assistant", assistantNull["role"]!.GetValue<string>());
        Assert.Null(assistantNull["content"]);

        var tool = CascadeChatMessage.Tool("42 items", "call_abc");
        Assert.Equal("tool", tool["role"]!.GetValue<string>());
        Assert.Equal("42 items", tool["content"]!.GetValue<string>());
        Assert.Equal("call_abc", tool["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public void FromToolSchemas_ConvertsFlatRealtimeShapeToNestedChatCompletionsShape()
    {
        var schemas = new List<JsonObject>
        {
            new()
            {
                ["type"] = "function",
                ["name"] = "search",
                ["description"] = "search the menu",
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            },
        };

        var definitions = CascadeToolDefinitions.FromToolSchemas(schemas);

        var def = Assert.Single(definitions);
        Assert.Equal("function", def["type"]!.GetValue<string>());
        var function = def["function"]!.AsObject();
        Assert.Equal("search", function["name"]!.GetValue<string>());
        Assert.Equal("search the menu", function["description"]!.GetValue<string>());
        Assert.Equal("object", function["parameters"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void FromToolSchemas_MissingDescriptionOrParameters_DefaultsToEmptyStringAndEmptyObject()
    {
        var schemas = new List<JsonObject> { new() { ["name"] = "get_order" } };

        var def = Assert.Single(CascadeToolDefinitions.FromToolSchemas(schemas));

        var function = def["function"]!.AsObject();
        Assert.Equal("", function["description"]!.GetValue<string>());
        Assert.NotNull(function["parameters"]);
        Assert.Empty(function["parameters"]!.AsObject());
    }

    [Fact]
    public void FromToolSchemas_EmptyInputProducesEmptyOutput()
    {
        Assert.Empty(CascadeToolDefinitions.FromToolSchemas([]));
    }
}

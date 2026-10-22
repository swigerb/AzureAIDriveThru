using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backend.Tests.Cascade;

public sealed class CascadeDiagnosticCaptureTests
{
    private const string Oid = "11111111-1111-1111-1111-111111111111";
    private const string Marker = "22222222222222222222222222222222";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T22:30:00Z");

    private static CascadeDiagnosticGate Gate(FakeTimeProvider clock, string? key = null, string? value = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["CascadeDiagnostics:Enabled"] = "true",
            ["CascadeDiagnostics:OperatorObjectId"] = Oid,
            ["CascadeDiagnostics:RunMarker"] = Marker,
            ["CascadeDiagnostics:ExpiresUtc"] = Now.AddMinutes(10).ToString("O"),
        };
        if (key is not null)
            settings["CascadeDiagnostics:" + key] = value;
        return new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), clock);
    }

    private static CascadeDiagnosticCapture? Claim(
        CascadeDiagnosticGate gate, RecordingLogger logger, string? oid = Oid, string marker = Marker,
        bool entra = true, bool authenticated = true, string persona = "tacobell",
        string pipeline = "cascade") =>
        gate.TryClaim(entra, authenticated, oid, marker, persona, pipeline, "synthetic-session", logger);

    [Theory]
    [InlineData("Enabled", null)]
    [InlineData("Enabled", "false")]
    [InlineData("OperatorObjectId", "")]
    [InlineData("RunMarker", "")]
    [InlineData("RunMarker", "00000000000000000000000000000000")]
    [InlineData("Enabled", "invalid")]
    [InlineData("ExpiresUtc", "bad")]
    [InlineData("ExpiresUtc", "2026-10-22T22:29:00Z")]
    [InlineData("ExpiresUtc", "2026-10-22T22:46:00Z")]
    public void Configuration_fails_closed(string key, string? value)
    {
        var logger = new RecordingLogger();
        Assert.Null(Claim(Gate(new(Now), key, value), logger));
        Assert.Equal(value is null || key == "Enabled" && value == "false" ? 0 : 1, logger.Warnings.Count);
    }

    [Fact]
    public void Identity_marker_and_pipeline_must_match_before_single_claim()
    {
        var logger = new RecordingLogger();
        var gate = Gate(new(Now));
        Assert.Null(Claim(gate, logger, oid: null));
        Assert.Null(Claim(gate, logger, oid: "33333333-3333-3333-3333-333333333333"));
        Assert.Null(Claim(gate, logger, marker: "wrong"));
        Assert.Null(Claim(gate, logger, entra: false));
        Assert.Null(Claim(gate, logger, authenticated: false));
        Assert.Null(Claim(gate, logger, persona: "sonic"));
        Assert.Null(Claim(gate, logger, pipeline: "realtime"));
        Assert.NotNull(Claim(gate, logger));
        Assert.Null(Claim(gate, logger));
    }

    [Fact]
    public void Concurrent_claims_cannot_capture_two_sessions()
    {
        var gate = Gate(new(Now));
        var claims = new System.Collections.Concurrent.ConcurrentBag<CascadeDiagnosticCapture>();
        Parallel.For(0, 20, _ =>
        {
            var capture = Claim(gate, new());
            if (capture is not null)
                claims.Add(capture);
        });
        Assert.Single(claims);
    }

    [Fact]
    public async Task Actual_chat_client_records_choice_finish_reason_without_changing_wire_request()
    {
        const string response = """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"(Note: The assistant mistakenly sent two messages; ignore the duplicate.)"}}]}""";
        var handler = new QueuedFoundryHttpHandler()
            .Enqueue(System.Net.HttpStatusCode.OK, response)
            .Enqueue(System.Net.HttpStatusCode.OK, response);
        var client = new FoundryChatClient(new HttpClient(handler), "https://fake.example",
            new StaticBearerTokenProvider("NEVER-COPY-AUTH"));
        var logger = new RecordingLogger();
        var capture = Claim(Gate(new(Now)), logger)!;
        var messages = new[] { CascadeChatMessage.User("NEVER-COPY-GUEST") };

        var baseline = await client.CompleteAsync(messages, "fake", [], CancellationToken.None);
        var diagnosed = await client.CompleteAsync(messages, "fake", [], CancellationToken.None, capture);

        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
        Assert.Equal(baseline.ToJsonString(), diagnosed.ToJsonString());
        var output = string.Join("\n", logger.Lines);
        Assert.Contains("ignore the duplicate", output);
        Assert.Contains("\"finishReason\":\"stop\"", output);
        Assert.DoesNotContain("NEVER-COPY", output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sanitization_timeout_disables_capture_without_changing_actual_chat_wire_or_response(bool duringRequest)
    {
        const string response = """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"What drink would you like?"}}]}""";
        var handler = new QueuedFoundryHttpHandler()
            .Enqueue(System.Net.HttpStatusCode.OK, response)
            .Enqueue(System.Net.HttpStatusCode.OK, response);
        var client = new FoundryChatClient(new HttpClient(handler), "https://fake.example",
            new StaticBearerTokenProvider("NEVER-COPY-AUTH"));
        var logger = new RecordingLogger();
        var attempts = 0;
        var capture = new CascadeDiagnosticCapture("synthetic", Now.AddMinutes(10), new FakeTimeProvider(Now),
            logger, text =>
            {
                attempts++;
                throw new System.Text.RegularExpressions.RegexMatchTimeoutException(
                    "NEVER-COPY-INPUT", "NEVER-COPY-PATTERN", TimeSpan.FromMilliseconds(100));
            });
        var messages = new[]
        {
            duringRequest ? CascadeChatMessage.Assistant("NEVER-COPY-INPUT") : CascadeChatMessage.User("NEVER-COPY-GUEST"),
        };
        var baseline = await client.CompleteAsync(messages, "fake", [], CancellationToken.None);
        var actual = await client.CompleteAsync(messages, "fake", [], CancellationToken.None, capture);
        capture.Emission("tts.started");
        capture.Request(messages);
        capture.Choice(actual, "stop");

        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
        Assert.Equal(baseline.ToJsonString(), actual.ToJsonString());
        Assert.Equal(1, attempts);
        Assert.Equal(duringRequest ? 0 : 1, logger.Lines.Count);
        Assert.DoesNotContain("NEVER-COPY", string.Join("\n", logger.Lines));
        Assert.Equal("Synthetic cascade diagnostic capture stopped: sanitization timed out.",
            Assert.Single(logger.Warnings));
        Assert.DoesNotContain("NEVER-COPY", string.Join("\n", logger.Warnings));
    }

    [Fact]
    public void Captures_boundary_without_changing_messages_or_copying_private_inputs()
    {
        var logger = new RecordingLogger();
        var capture = Claim(Gate(new(Now)), logger)!;
        logger.Lines.Clear();
        capture.ResponseId = "response-one";
        var call = new JsonObject
        {
            ["id"] = "call-one", ["function"] = new JsonObject
            {
                ["name"] = "update_order", ["arguments"] = """{"code":"NEVER-COPY-ARGUMENT"}""",
            },
        };
        var messages = new List<JsonObject>
        {
            CascadeChatMessage.System("NEVER-COPY-SYSTEM"),
            CascadeChatMessage.User("NEVER-COPY-GUEST"),
            new() { ["role"] = "assistant", ["content"] = "Adding your combo.", ["tool_calls"] = new JsonArray(call) },
            CascadeChatMessage.Tool("NEVER-COPY-TOOL-CODE", "call-one"),
        };
        var original = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()).ToJsonString();
        capture.Request(messages);
        var choice = CascadeChatMessage.Assistant("(Note: The assistant mistakenly sent two messages; ignore the duplicate.)");
        capture.Choice(choice, "stop");
        capture.Emission("tts.started");
        capture.Emission("response.done");
        var output = string.Join("\n", logger.Lines);
        Assert.DoesNotContain("NEVER-COPY", output);
        Assert.DoesNotContain("\"arguments\"", output);
        Assert.Contains("ignore the duplicate", output);
        Assert.Contains("response-one", output);
        Assert.Contains("update_order", output);
        Assert.Contains("stop", output);
        Assert.Equal(original, new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()).ToJsonString());
        var request = JsonNode.Parse(logger.Lines[0])!;
        var snapshot = request["messages"]!.AsArray();
        Assert.Equal(new[] { "system", "user", "assistant", "tool" },
            snapshot.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal(snapshot[2]!["toolCalls"]![0]!["idHash"]!.GetValue<string>(),
            snapshot[3]!["toolCallIdHash"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("DEMO-PLUS")]
    [InlineData("123456")]
    [InlineData("AB123C")]
    [InlineData("name@example.com")]
    [InlineData("https://example.com/?token=anything")]
    [InlineData("secret:abc")]
    [InlineData("token=abc")]
    [InlineData("Bearer_abcdefghijklmnopqrstuvwxyz")]
    public void Redacts_synthetic_echoes(string sensitive)
    {
        Assert.DoesNotContain(sensitive, CascadeDiagnosticCapture.Sanitize("Echo " + sensitive));
    }

    [Fact]
    public void Expiry_event_and_payload_limits_are_enforced()
    {
        var logger = new RecordingLogger();
        var clock = new FakeTimeProvider(Now);
        var capture = Claim(Gate(clock), logger)!;
        logger.Lines.Clear();
        capture.Choice(CascadeChatMessage.Assistant(new string('x', 5000)), "stop");
        Assert.True(logger.Lines[0].Length < 1500);
        for (var i = 0; i < 200; i++)
            capture.Emission("tts.started");
        Assert.Equal(159, logger.Lines.Count);
        var expiredLogger = new RecordingLogger();
        var expiring = Claim(Gate(clock), expiredLogger)!;
        expiredLogger.Lines.Clear();
        clock.Advance(TimeSpan.FromMinutes(10));
        expiring.Emission("response.done");
        Assert.Empty(expiredLogger.Lines);
    }

    internal sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
                return;
            }
            var fields = (IEnumerable<KeyValuePair<string, object?>>)(object)state!;
            Lines.Add(fields.Single(p => p.Key == "Diagnostic").Value!.ToString()!);
        }
    }
}

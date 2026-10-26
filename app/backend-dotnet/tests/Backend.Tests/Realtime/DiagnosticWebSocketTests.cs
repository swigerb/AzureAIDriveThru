using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Cascade;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Backend.Tests.Realtime;

public sealed class DiagnosticWebSocketTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.Parse("2026-10-22T22:30:00Z", CultureInfo.InvariantCulture);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static (byte[] Data, bool EndOfMessage, WebSocketMessageType MessageType) Text(string value, bool end = true)
        => (Encoding.UTF8.GetBytes(value), end, WebSocketMessageType.Text);
    private static CascadeDiagnosticCapture Capture(CascadeDiagnosticCaptureTests.RecordingLogger logger, Func<string, string>? sanitize = null)
        => new("synthetic", s_now.AddMinutes(10), new FakeTimeProvider(s_now), logger, sanitize);

    [Fact]
    public void Realtime_configuration_is_separate_and_gate_does_not_allow_other_pipeline()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RealtimeDiagnostics:Enabled"] = "true",
            ["RealtimeDiagnostics:OperatorObjectId"] = "11111111-1111-1111-1111-111111111111",
            ["RealtimeDiagnostics:RunMarker"] = "22222222222222222222222222222222",
            ["RealtimeDiagnostics:ExpiresUtc"] = s_now.AddMinutes(10).ToString("O"),
        }).Build();
        var gate = new CascadeDiagnosticGate(configuration, new FakeTimeProvider(s_now), "RealtimeDiagnostics", "realtime");
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        CascadeDiagnosticCapture? Claim(string pipeline) => gate.TryClaim(true, true,
            "11111111-1111-1111-1111-111111111111", "22222222222222222222222222222222", "tacobell", pipeline, "one", logger);
        Assert.Null(Claim("cascade"));
        Assert.NotNull(Claim("realtime"));
        Assert.Null(Claim("realtime"));
    }

    [Fact]
    public async Task Actual_fragmented_wire_retains_applied_policy_and_phase_boundaries_without_private_text()
    {
        const string Policy = "PRIVATE-SYSTEM-INSTRUCTIONS";
        string outbound = new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["instructions"] = Policy,
                ["tools"] = new JsonArray(new JsonObject { ["name"] = "get_order", ["parameters"] = new JsonObject() }),
            },
        }.ToJsonString();
        string ack = outbound.Replace("session.update", "session.updated", StringComparison.Ordinal);
        const string Done = """{"type":"response.done","response":{"id":"PRIVATE-RESPONSE-ID","status":"completed","output":[{"id":"PRIVATE-ITEM-ID","type":"message","role":"assistant","phase":"commentary","content":[{"type":"output_audio","transcript":"Let me check that. DEMO-PLUS"}]},{"type":"function_call","call_id":"PRIVATE-CALL-ID","name":"get_order","arguments":"PRIVATE-ARGS"}]}}""";
        var inner = new FakeWebSocket([Text(ack[..20], false), Text(ack[20..]), Text(Done)]);
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        using var socket = new DiagnosticWebSocket(inner, Capture(logger), true);
        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(outbound[..12])), WebSocketMessageType.Text, false, TestContext.Current.CancellationToken);
        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(outbound[12..])), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        WebSocketFrame? first = await WebSocketFrameReader.ReadMessageAsync(socket, TestContext.Current.CancellationToken);
        WebSocketFrame? second = await WebSocketFrameReader.ReadMessageAsync(socket, TestContext.Current.CancellationToken);
        Assert.Equal(ack, Encoding.UTF8.GetString(first!.Payload));
        Assert.Equal(Done, Encoding.UTF8.GetString(second!.Payload));
        Assert.Equal(outbound, string.Concat(inner.SentMessages.Select(s => Encoding.UTF8.GetString(s.Data))));
        JsonNode[] records = [.. logger.Lines.Select(s => JsonNode.Parse(s)!)];
        Assert.Equal(Hash(Policy), records[0]["instructionsHash"]!.GetValue<string>());
        Assert.Equal(records[0]["instructionsHash"]!.GetValue<string>(), records[1]["instructionsHash"]!.GetValue<string>());
        Assert.Equal("commentary", records[2]["output"]![0]!["phase"]!.GetValue<string>());
        Assert.Equal(Hash("PRIVATE-CALL-ID"), records[2]["output"]![1]!["callIdHash"]!.GetValue<string>());
        string logs = string.Join("\n", logger.Lines);
        Assert.Contains("Let me check that", logs);
        Assert.DoesNotContain("PRIVATE-", logs);
        Assert.DoesNotContain("DEMO-PLUS", logs);
        Assert.DoesNotContain("arguments", logs);
    }

    [Fact]
    public async Task Resume_disables_capture_and_audio_guest_tool_results_are_not_copied()
    {
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        CascadeDiagnosticCapture capture = Capture(logger);
        var inner = new FakeWebSocket([Text("""{"type":"extension.resume","resume_id":"PRIVATE-RESUME"}""")]);
        using var browser = new DiagnosticWebSocket(inner, capture, false);
        await WebSocketFrameReader.ReadMessageAsync(browser, TestContext.Current.CancellationToken);
        capture.RealtimeFrame(JsonNode.Parse("""{"type":"response.done","response":{"output":[]}}""")!.AsObject(), false);
        Assert.Empty(logger.Lines);
        CascadeDiagnosticCapture fresh = Capture(logger);
        foreach (string? frame in new[]
        {
            """{"type":"input_audio_buffer.append","audio":"PRIVATE-AUDIO"}""",
            """{"type":"conversation.item.input_audio_transcription.completed","transcript":"PRIVATE-GUEST"}""",
            """{"type":"conversation.item.create","item":{"type":"function_call_output","call_id":"PRIVATE-CALL","output":"PRIVATE-TOOL"}}""",
        })
        {
            fresh.RealtimeFrame(JsonNode.Parse(frame)!.AsObject(), true);
        }

        Assert.Single(logger.Lines);
        Assert.DoesNotContain("PRIVATE-", logger.Lines[0]);
    }

    [Fact]
    public async Task Sanitizer_timeout_never_changes_forwarded_frame()
    {
        const string Frame = """{"type":"response.create","response":{"instructions":"PRIVATE-SYSTEM","tools":[]}}""";
        var inner = new FakeWebSocket([]);
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        using var socket = new DiagnosticWebSocket(inner, Capture(logger, _ =>
            throw new System.Text.RegularExpressions.RegexMatchTimeoutException()), true);
        await FramePump.SendTextAsync(socket, Frame, TestContext.Current.CancellationToken);
        Assert.Equal(Frame, Encoding.UTF8.GetString(Assert.Single(inner.SentMessages).Data));
        Assert.Empty(logger.Lines);
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public async Task Invalid_and_oversized_frames_are_forwarded_unchanged_without_unbounded_capture()
    {
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        var inner = new FakeWebSocket([]);
        using var socket = new DiagnosticWebSocket(inner, Capture(logger), true);
        string oversized = new JsonObject
        {
            ["type"] = "response.create",
            ["response"] = new JsonObject { ["instructions"] = new string('x', 140000) },
        }.ToJsonString();
        await FramePump.SendTextAsync(socket, oversized, TestContext.Current.CancellationToken);
        Assert.Equal(oversized, Encoding.UTF8.GetString(inner.SentMessages[0].Data));
        Assert.Empty(logger.Lines);
        Assert.Equal("Synthetic cascade diagnostic capture stopped: text frame limit reached.", Assert.Single(logger.Warnings));
        await FramePump.SendTextAsync(socket, """{"type":"response.cancel"}""", TestContext.Current.CancellationToken);
        Assert.Empty(logger.Lines);
        await FramePump.SendTextAsync(socket, "not-json", TestContext.Current.CancellationToken);
        await FramePump.SendTextAsync(socket, """{"type":"response.cancel"}""", TestContext.Current.CancellationToken);
        Assert.Equal(4, inner.SentMessages.Count);
        Assert.Empty(logger.Lines);
    }
}

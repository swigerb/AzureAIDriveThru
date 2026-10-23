using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Shared;
using Backend.Tests.Cascade;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Backend.Tests.Realtime;

public sealed class DiagnosticWebSocketTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T22:30:00Z");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static (byte[] Data, bool EndOfMessage, WebSocketMessageType MessageType) Text(string value, bool end = true)
        => (Encoding.UTF8.GetBytes(value), end, WebSocketMessageType.Text);
    private static CascadeDiagnosticCapture Capture(CascadeDiagnosticCaptureTests.RecordingLogger logger, Func<string, string>? sanitize = null)
        => new("synthetic", Now.AddMinutes(10), new FakeTimeProvider(Now), logger, sanitize);

    [Fact]
    public void Realtime_configuration_is_separate_and_gate_does_not_allow_other_pipeline()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RealtimeDiagnostics:Enabled"] = "true",
            ["RealtimeDiagnostics:OperatorObjectId"] = "11111111-1111-1111-1111-111111111111",
            ["RealtimeDiagnostics:RunMarker"] = "22222222222222222222222222222222",
            ["RealtimeDiagnostics:ExpiresUtc"] = Now.AddMinutes(10).ToString("O"),
        }).Build();
        var gate = new CascadeDiagnosticGate(configuration, new FakeTimeProvider(Now), "RealtimeDiagnostics", "realtime");
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
        const string policy = "PRIVATE-SYSTEM-INSTRUCTIONS";
        var outbound = new JsonObject
        {
            ["type"] = "session.update", ["session"] = new JsonObject
            {
                ["instructions"] = policy,
                ["tools"] = new JsonArray(new JsonObject { ["name"] = "get_order", ["parameters"] = new JsonObject() }),
            },
        }.ToJsonString();
        var ack = outbound.Replace("session.update", "session.updated", StringComparison.Ordinal);
        const string done = """{"type":"response.done","response":{"id":"PRIVATE-RESPONSE-ID","status":"completed","output":[{"id":"PRIVATE-ITEM-ID","type":"message","role":"assistant","phase":"commentary","content":[{"type":"output_audio","transcript":"Let me check that. DEMO-PLUS"}]},{"type":"function_call","call_id":"PRIVATE-CALL-ID","name":"get_order","arguments":"PRIVATE-ARGS"}]}}""";
        var inner = new FakeWebSocket([Text(ack[..20], false), Text(ack[20..]), Text(done)]);
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        using var socket = new DiagnosticWebSocket(inner, Capture(logger), true);
        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(outbound[..12])), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(outbound[12..])), WebSocketMessageType.Text, true, CancellationToken.None);
        var first = await WebSocketFrameReader.ReadMessageAsync(socket, CancellationToken.None);
        var second = await WebSocketFrameReader.ReadMessageAsync(socket, CancellationToken.None);
        Assert.Equal(ack, Encoding.UTF8.GetString(first!.Payload));
        Assert.Equal(done, Encoding.UTF8.GetString(second!.Payload));
        Assert.Equal(outbound, string.Concat(inner.SentMessages.Select(s => Encoding.UTF8.GetString(s.Data))));
        var records = logger.Lines.Select(s => JsonNode.Parse(s)!).ToArray();
        Assert.Equal(Hash(policy), records[0]["instructionsHash"]!.GetValue<string>());
        Assert.Equal(records[0]["instructionsHash"]!.GetValue<string>(), records[1]["instructionsHash"]!.GetValue<string>());
        Assert.Equal("commentary", records[2]["output"]![0]!["phase"]!.GetValue<string>());
        Assert.Equal(Hash("PRIVATE-CALL-ID"), records[2]["output"]![1]!["callIdHash"]!.GetValue<string>());
        var logs = string.Join("\n", logger.Lines);
        Assert.Contains("Let me check that", logs);
        Assert.DoesNotContain("PRIVATE-", logs);
        Assert.DoesNotContain("DEMO-PLUS", logs);
        Assert.DoesNotContain("arguments", logs);
    }

    [Fact]
    public async Task Resume_disables_capture_and_audio_guest_tool_results_are_not_copied()
    {
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        var capture = Capture(logger);
        var inner = new FakeWebSocket([Text("""{"type":"extension.resume","resume_id":"PRIVATE-RESUME"}""")]);
        using var browser = new DiagnosticWebSocket(inner, capture, false);
        await WebSocketFrameReader.ReadMessageAsync(browser, CancellationToken.None);
        capture.RealtimeFrame(JsonNode.Parse("""{"type":"response.done","response":{"output":[]}}""")!.AsObject(), false);
        Assert.Empty(logger.Lines);
        var fresh = Capture(logger);
        foreach (var frame in new[]
        {
            """{"type":"input_audio_buffer.append","audio":"PRIVATE-AUDIO"}""",
            """{"type":"conversation.item.input_audio_transcription.completed","transcript":"PRIVATE-GUEST"}""",
            """{"type":"conversation.item.create","item":{"type":"function_call_output","call_id":"PRIVATE-CALL","output":"PRIVATE-TOOL"}}""",
        })
            fresh.RealtimeFrame(JsonNode.Parse(frame)!.AsObject(), true);
        Assert.Single(logger.Lines);
        Assert.DoesNotContain("PRIVATE-", logger.Lines[0]);
    }

    [Fact]
    public async Task Sanitizer_timeout_never_changes_forwarded_frame()
    {
        const string frame = """{"type":"response.create","response":{"instructions":"PRIVATE-SYSTEM","tools":[]}}""";
        var inner = new FakeWebSocket([]);
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        using var socket = new DiagnosticWebSocket(inner, Capture(logger, _ =>
            throw new System.Text.RegularExpressions.RegexMatchTimeoutException()), true);
        await FramePump.SendTextAsync(socket, frame, CancellationToken.None);
        Assert.Equal(frame, Encoding.UTF8.GetString(Assert.Single(inner.SentMessages).Data));
        Assert.Empty(logger.Lines);
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public async Task Invalid_and_oversized_frames_are_forwarded_unchanged_without_unbounded_capture()
    {
        var logger = new CascadeDiagnosticCaptureTests.RecordingLogger();
        var inner = new FakeWebSocket([]);
        using var socket = new DiagnosticWebSocket(inner, Capture(logger), true);
        var oversized = new JsonObject
        {
            ["type"] = "response.create", ["response"] = new JsonObject { ["instructions"] = new string('x', 140000) },
        }.ToJsonString();
        await FramePump.SendTextAsync(socket, oversized, CancellationToken.None);
        Assert.Equal(oversized, Encoding.UTF8.GetString(inner.SentMessages[0].Data));
        Assert.Empty(logger.Lines);
        await FramePump.SendTextAsync(socket, "not-json", CancellationToken.None);
        await FramePump.SendTextAsync(socket, """{"type":"response.cancel"}""", CancellationToken.None);
        Assert.Equal(3, inner.SentMessages.Count);
        Assert.Empty(logger.Lines);
    }
}

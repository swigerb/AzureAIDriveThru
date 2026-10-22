using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Backend.Cascade;

/// <summary>Temporary, fail-closed capture for one authenticated synthetic session.</summary>
internal sealed class CascadeDiagnosticGate(IConfiguration configuration, TimeProvider clock)
{
    private int _claimed;

    public CascadeDiagnosticCapture? TryClaim(
        bool entraMode, bool authenticated, string? oid, string marker, string persona,
        string pipeline, string sessionId, ILogger logger)
    {
        var settings = configuration.GetSection("CascadeDiagnostics");
        var expectedOid = settings["OperatorObjectId"];
        var expectedMarker = settings["RunMarker"];
        var now = clock.GetUtcNow();
        if (string.IsNullOrEmpty(settings["Enabled"]) || settings["Enabled"] == "false")
            return null;
        if (settings["Enabled"] != "true"
            || !Guid.TryParse(expectedOid, out var configuredOid) || configuredOid == Guid.Empty
            || !Guid.TryParseExact(expectedMarker, "N", out var runId) || runId == Guid.Empty
            || !DateTimeOffset.TryParse(settings["ExpiresUtc"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var expires)
            || expires <= now || expires - now > TimeSpan.FromMinutes(15))
        {
            logger.LogWarning("Synthetic cascade diagnostics disabled: invalid or expired configuration.");
            return null;
        }
        if (!entraMode || !authenticated
            || !Guid.TryParse(oid, out var actualOid) || configuredOid != actualOid
            || !string.Equals(marker, expectedMarker, StringComparison.Ordinal)
            || persona != "tacobell" || pipeline != "cascade")
        {
            if (!string.IsNullOrEmpty(marker))
                logger.LogWarning("Synthetic cascade diagnostic request rejected: scope mismatch.");
            return null;
        }
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        {
            logger.LogWarning("Synthetic cascade diagnostic request rejected: process claim already used.");
            return null;
        }
        var capture = new CascadeDiagnosticCapture(sessionId, expires, clock, logger);
        capture.Emission("claimed");
        return capture;
    }
}

internal sealed class CascadeDiagnosticCapture(
    string sessionId, DateTimeOffset expires, TimeProvider clock, ILogger logger,
    Func<string, string>? sanitize = null)
{
    private int _events;
    private int _stopped;
    public string SessionId { get; } = sessionId;
    public string? ResponseId { get; set; }

    public void Request(IReadOnlyList<JsonObject> messages)
        => Capture(() => RequestCore(messages));

    private void RequestCore(IReadOnlyList<JsonObject> messages)
    {
        var snapshot = new JsonArray();
        foreach (var message in messages.TakeLast(64))
        {
            var role = message["role"]?.GetValue<string>();
            var content = message["content"]?.GetValue<string>() ?? "";
            var entry = new JsonObject
            {
                ["role"] = role,
                ["contentLength"] = content.Length,
            };
            // User/system/tool text may contain codes or retrieved data. Never copy it.
            if (role == "assistant")
                entry["content"] = SanitizeContent(content);
            if (message["tool_call_id"] is JsonValue toolId)
                entry["toolCallIdHash"] = Hash(toolId.GetValue<string>());
            if (message["tool_calls"] is JsonArray calls)
                entry["toolCalls"] = ToolCalls(calls);
            snapshot.Add(entry);
        }
        while (snapshot.Count > 1 && snapshot.ToJsonString().Length > 12000)
            snapshot.RemoveAt(0);
        Write("request", new JsonObject
        {
            ["messages"] = snapshot,
            ["messageCount"] = messages.Count,
            ["retainedMessageCount"] = snapshot.Count,
        });
    }

    public void Choice(JsonObject message, string? finishReason) => Capture(() => Write("choice", new JsonObject
    {
        ["content"] = SanitizeContent(message["content"]?.GetValue<string>() ?? ""),
        ["toolCalls"] = ToolCalls(message["tool_calls"] as JsonArray),
        ["finishReason"] = SanitizeContent(finishReason ?? ""),
    }));

    public void Emission(string stage) => Write(stage, new JsonObject());

    private void Write(string stage, JsonObject payload)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return;
        var sequence = Interlocked.Increment(ref _events);
        if (clock.GetUtcNow() >= expires || sequence > 160)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                logger.LogWarning("Synthetic cascade diagnostic capture stopped: expiry or event limit reached.");
            return;
        }
        payload["sessionId"] = SessionId;
        payload["responseId"] = ResponseId;
        payload["sequence"] = sequence;
        payload["stage"] = stage;
        logger.LogInformation("Synthetic cascade diagnostic {Diagnostic}", payload.ToJsonString());
    }

    private JsonArray ToolCalls(JsonArray? calls)
    {
        var result = new JsonArray();
        foreach (var call in calls?.Take(16) ?? [])
            result.Add(new JsonObject
            {
                ["idHash"] = Hash(call?["id"]?.GetValue<string>() ?? ""),
                ["name"] = SanitizeContent(call?["function"]?["name"]?.GetValue<string>() ?? ""),
            });
        return result;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private string SanitizeContent(string text) => (sanitize ?? Sanitize)(text);

    private void Capture(Action action)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return;
        try
        {
            action();
        }
        catch (RegexMatchTimeoutException)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                logger.LogWarning("Synthetic cascade diagnostic capture stopped: sanitization timed out.");
        }
    }

    internal static string Sanitize(string text)
    {
        // Do not retain arbitrary opaque values, URLs, addresses, codes or credentials echoed
        // into synthetic assistant speech. Arguments and non-assistant text never enter here.
        var value = text[..Math.Min(text.Length, 1200)];
        value = Regex.Replace(value,
            @"(?i)(?:https?://|www\.)\S+|\b[\w.+-]+@[\w.-]+\b|\bDEMO-[\w-]+\b|(?:\+?\d[\d ().-]{6,}\d)|\b(?=[A-Za-z0-9-]*\d)[A-Za-z0-9-]{4,}\b|\b[A-Za-z0-9_+/=-]{20,}\b",
            "[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return Regex.Replace(value,
            @"(?i)\b(?:code|token|password|secret|key|authorization)(?:\s*[:=]\s*|\s+)\S+",
            "[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}

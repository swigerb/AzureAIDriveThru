using System.Text.Json.Nodes;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>Byte-for-byte port tests for rtmt.py's `_SessionUpdateGuard` -- mirrors
/// app/backend/tests/test_session_bootstrap.py's `SessionUpdateGuardTests` cases.</summary>
public sealed class SessionUpdateGuardTests
{
    private static JsonObject Error(string? eventId = null, string? param = null, string type = "invalid_request_error") =>
        new()
        {
            ["type"] = "error",
            ["error"] = new JsonObject
            {
                ["type"] = type,
                ["code"] = "invalid_value",
                ["param"] = param,
                ["event_id"] = eventId,
            },
        };

    [Fact]
    public void Track_AddsEventIdAndKeepsAnExistingOne()
    {
        var guard = new SessionUpdateGuard();

        var added = JsonNode.Parse(guard.Track("""{"type":"session.update","session":{}}"""))!.AsObject();
        Assert.StartsWith("sonic_su_", added["event_id"]!.GetValue<string>());

        var kept = JsonNode.Parse(guard.Track("""{"type":"session.update","event_id":"mine","session":{}}"""))!.AsObject();
        Assert.Equal("mine", kept["event_id"]!.GetValue<string>());
    }

    [Fact]
    public void CorrelationRules_MatchPythonExactly()
    {
        var guard = new SessionUpdateGuard();

        Assert.Null(guard.Correlate(Error())); // nothing in flight

        guard.Stamp(new JsonObject { ["type"] = "session.update", ["event_id"] = "su1", ["session"] = new JsonObject() });
        Assert.Null(guard.Correlate(Error(eventId: "client_evt"))); // explicitly someone else's
        Assert.Null(guard.Correlate(Error(param: "item_id"))); // not a session field
        Assert.Null(guard.Correlate(Error(type: "server_error"))); // not a validation error
        Assert.Equal("su1", guard.Correlate(Error(param: "session.reasoning")));

        guard.Stamp(new JsonObject { ["type"] = "session.update", ["event_id"] = "su2", ["session"] = new JsonObject() });
        guard.OnSessionUpdated();
        Assert.Null(guard.Correlate(Error())); // su2 was acknowledged
        Assert.Equal("su2", guard.Correlate(Error(eventId: "su2"))); // echoed id always correlates
    }

    [Fact]
    public void ClaimFallback_IsTrueExactlyOncePerOriginal()
    {
        var guard = new SessionUpdateGuard();

        Assert.True(guard.ClaimFallback("su1"));
        Assert.False(guard.ClaimFallback("su1"));
        Assert.True(guard.ClaimFallback("su2"));
    }

    [Fact]
    public void Stamp_DoesNotCrashOnADictEventId()
    {
        var guard = new SessionUpdateGuard();
        var message = new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = new JsonObject { ["a"] = 1 },
            ["session"] = new JsonObject(),
        };

        var stamped = guard.Stamp(message);

        Assert.IsAssignableFrom<JsonValue>(stamped["event_id"]);
        Assert.NotEmpty(stamped["event_id"]!.GetValue<string>());
    }

    [Fact]
    public void Stamp_DoesNotCrashOnAListEventId()
    {
        var guard = new SessionUpdateGuard();
        var message = new JsonObject
        {
            ["type"] = "session.update",
            ["event_id"] = new JsonArray("a"),
            ["session"] = new JsonObject(),
        };

        var stamped = guard.Stamp(message);

        Assert.IsAssignableFrom<JsonValue>(stamped["event_id"]);
        Assert.NotEmpty(stamped["event_id"]!.GetValue<string>());
    }

    [Fact]
    public void Stamp_IgnoresAnEmptyStringEventIdAndGeneratesItsOwn()
    {
        var guard = new SessionUpdateGuard();
        var message = new JsonObject { ["type"] = "session.update", ["event_id"] = "", ["session"] = new JsonObject() };

        var stamped = guard.Stamp(message);

        Assert.NotEqual("", stamped["event_id"]!.GetValue<string>());
    }

    [Fact]
    public void Correlate_ExcludesRateLimitErrorsEvenWithoutEventId()
    {
        var guard = new SessionUpdateGuard();
        guard.Stamp(new JsonObject { ["type"] = "session.update", ["event_id"] = "su1", ["session"] = new JsonObject() });

        var rateLimitError = new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["code"] = "rate_limit_exceeded" },
        };

        Assert.Null(guard.Correlate(rateLimitError));
    }

    [Fact]
    public void Stamp_EvictsOldestEntryPastSixtyFourTracked()
    {
        var guard = new SessionUpdateGuard();
        for (var i = 0; i < 65; i++)
        {
            guard.Stamp(new JsonObject { ["type"] = "session.update", ["event_id"] = $"su{i}", ["session"] = new JsonObject() });
        }

        Assert.Null(guard.OriginalOf("su0")); // evicted
        Assert.Empty(guard.PayloadOf("su0"));
        Assert.Equal(guard.OriginalOf("su64"), guard.OriginalOf("su64")); // still tracked (no throw)
    }
}

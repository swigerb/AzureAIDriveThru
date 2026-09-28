using System.Reflection;
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

    /// <summary>PR #140 R2: `Stamp`/`Track` run on the browser relay loop while `Correlate`/
    /// `OnSessionUpdated` run on the upstream relay loop, and both loops run truly in parallel on
    /// the thread pool -- there is no single-threaded asyncio loop to serialize them the way
    /// rtmt.py has. Two real OS threads, released together by a <see cref="Barrier"/> (no sleeps,
    /// so the iterations actually interleave), hammer 10k iterations each. Removing the locks in
    /// <c>SessionUpdateGuard</c> makes this fail (an <c>InvalidOperationException</c> from
    /// mutating a <c>Dictionary</c>/<c>List</c> that another thread is enumerating, or a corrupted
    /// ring) within a handful of local runs.</summary>
    [Fact]
    public void Stamp_And_Track_Are_Threadsafe_Against_Correlate_And_OnSessionUpdated()
    {
        var guard = new SessionUpdateGuard();
        const int iterations = 10_000;
        using var barrier = new Barrier(2);
        Exception? browserException = null;
        Exception? upstreamException = null;

        var browserThread = new Thread(() =>
        {
            barrier.SignalAndWait();
            try
            {
                for (var i = 0; i < iterations; i++)
                {
                    guard.Stamp(new JsonObject { ["type"] = "session.update", ["session"] = new JsonObject { ["voice"] = "marin" } });
                    guard.Track("""{"type":"session.update","session":{"voice":"marin"}}""");
                }
            }
            catch (Exception ex)
            {
                browserException = ex;
            }
        });

        var upstreamThread = new Thread(() =>
        {
            barrier.SignalAndWait();
            try
            {
                for (var i = 0; i < iterations; i++)
                {
                    guard.Correlate(Error(param: "session.voice"));
                    guard.OnSessionUpdated();
                }
            }
            catch (Exception ex)
            {
                upstreamException = ex;
            }
        });

        browserThread.Start();
        upstreamThread.Start();
        browserThread.Join();
        upstreamThread.Join();

        Assert.Null(browserException);
        Assert.Null(upstreamException);

        // The ring stays bounded and internally consistent regardless of how the two threads'
        // iterations happened to interleave.
        var sentOrder = (List<string>)typeof(SessionUpdateGuard)
            .GetField("_sentOrder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(guard)!;
        var sent = (Dictionary<string, string?>)typeof(SessionUpdateGuard)
            .GetField("_sent", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(guard)!;
        Assert.True(sentOrder.Count <= 64, $"expected the ring to stay capped at 64, found {sentOrder.Count}");
        Assert.Equal(sentOrder.Count, sent.Count);
        Assert.Equal(sentOrder.ToHashSet().Count, sentOrder.Count); // no duplicate entries in the ring
    }
}

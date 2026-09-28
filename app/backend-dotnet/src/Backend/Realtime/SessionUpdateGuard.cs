using System.Text.Json.Nodes;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rtmt.py's `_SessionUpdateGuard` (issue #13's "event-id guard" acceptance
/// target). Tracks the session.updates sent on ONE upstream socket so a GA rejection can be
/// correlated back to them.
///
/// GA rejects an invalid session.update wholesale and reports it only as an `error` event. Most
/// rejections echo our `event_id` in `error.event_id`, but some do not (gpt-realtime-1.5
/// rejecting `reasoning` returns no event_id and no param), so an uncorrelated
/// invalid_request_error that arrives while one of our updates is still unacknowledged is
/// attributed to the oldest one -- the service processes client events in order.
///
/// Internally synchronized: `Stamp`/`Track` are called from the browser relay loop (client
/// session.update, set_voice) while `Correlate`, `OnSessionUpdated`, `OriginalOf`, `PayloadOf` and
/// `ClaimFallback` are called from the upstream relay loop, and both loops run truly in parallel on
/// the thread pool. Every public member takes an internal lock around the shared
/// Dictionary/List/Queue/HashSet state.
/// </summary>
public sealed class SessionUpdateGuard
{
    private const int MaxTracked = 64;

    private readonly object _sync = new();

    // Insertion-ordered event_id -> event_id of the original if this is a fallback, else null.
    // A plain Dictionary<> plus a separate ordering list stands in for Python's OrderedDict here.
    private readonly Dictionary<string, string?> _sent = new();
    private readonly List<string> _sentOrder = [];
    private readonly Dictionary<string, JsonObject> _payloads = new();
    private readonly Queue<string> _inFlight = new();
    private readonly HashSet<string> _fallbackSentFor = [];

    /// <summary>
    /// Ensures <paramref name="message"/> carries an event_id and starts tracking it. Mutates and
    /// returns <paramref name="message"/> in place, mirroring Python's `stamp`.
    /// </summary>
    public JsonObject Stamp(JsonObject message, string? fallbackOf = null)
    {
        // The candidate event_id must be a non-empty string -- it's used as a dict key below, so
        // anything else (a browser-forged {"event_id": {...}} or [...]) must not throw trying to
        // read it as a string; discard it and generate a fresh one instead, mirroring rtmt.py's
        // `isinstance(candidate, str) and candidate` guard.
        var candidateNode = message["event_id"];
        var candidate = candidateNode is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
        var eventId = !string.IsNullOrEmpty(candidate)
            ? candidate
            : EventIds.NewEventId(fallbackOf is not null ? "sonic_fallback" : "sonic_su");
        message["event_id"] = eventId;

        lock (_sync)
        {
            if (_sent.ContainsKey(eventId))
            {
                _sentOrder.Remove(eventId);
            }
            _sent[eventId] = fallbackOf;
            _sentOrder.Add(eventId);
            _payloads[eventId] = message["session"] as JsonObject ?? [];
            _inFlight.Enqueue(eventId);

            while (_sentOrder.Count > MaxTracked)
            {
                var oldest = _sentOrder[0];
                _sentOrder.RemoveAt(0);
                _sent.Remove(oldest);
                _payloads.Remove(oldest);
            }
        }

        return message;
    }

    /// <summary>`Stamp` for an already-serialised session.update payload.</summary>
    public string Track(string payload, string? fallbackOf = null)
    {
        var message = JsonNode.Parse(payload) as JsonObject
            ?? throw new InvalidOperationException("Tracked session.update payload was not a JSON object.");
        var hadId = message["event_id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id);
        Stamp(message, fallbackOf);
        return hadId ? payload : message.ToJsonString();
    }

    public void OnSessionUpdated()
    {
        lock (_sync)
        {
            if (_inFlight.Count > 0)
            {
                _inFlight.Dequeue();
            }
        }
    }

    /// <summary>Returns the event_id of our session.update this error rejects, or null.</summary>
    public string? Correlate(JsonObject errorEvent)
    {
        var err = errorEvent["error"] as JsonObject;
        var eventId = err?["event_id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) ? id : null;

        lock (_sync)
        {
            if (!string.IsNullOrEmpty(eventId))
            {
                if (!_sent.ContainsKey(eventId))
                {
                    return null;
                }
                // Queue<T> has no direct "remove this specific element" -- rebuild, same net effect
                // as Python's `deque.remove(event_id)` (a no-op if it isn't present).
                if (_inFlight.Contains(eventId))
                {
                    var rest = _inFlight.Where(inFlightId => inFlightId != eventId).ToArray();
                    _inFlight.Clear();
                    foreach (var inFlightId in rest)
                    {
                        _inFlight.Enqueue(inFlightId);
                    }
                }
                return eventId;
            }

            var param = err?["param"]?.GetValue<string>() ?? "";
            var errorType = err?["type"]?.GetValue<string>();
            // A rate limit is never a session.update rejection; only an echoed event_id (above) ties
            // one to an update.
            if (_inFlight.Count > 0 && errorType == "invalid_request_error" && !RateLimitDetection.IsRateLimitError(err)
                && (string.IsNullOrEmpty(param) || param.StartsWith("session", StringComparison.Ordinal)))
            {
                return _inFlight.Dequeue();
            }
            return null;
        }
    }

    public string? OriginalOf(string eventId)
    {
        lock (_sync)
        {
            return _sent.GetValueOrDefault(eventId);
        }
    }

    public JsonObject PayloadOf(string eventId)
    {
        lock (_sync)
        {
            return _payloads.GetValueOrDefault(eventId) ?? [];
        }
    }

    /// <summary>True exactly once per original session.update.</summary>
    public bool ClaimFallback(string eventId)
    {
        lock (_sync)
        {
            return _fallbackSentFor.Add(eventId);
        }
    }
}

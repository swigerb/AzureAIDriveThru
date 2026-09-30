using System.Text.Json.Nodes;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/session_manager.py's <c>SessionIdentifiers</c> / <c>emit_session_identifiers</c>
/// (issue #13's session-metadata acceptance target). A per-session opaque token plus a
/// monotonically-increasing round-trip counter -- the browser uses these to correlate its own UI
/// state with a specific backend session/turn across the wire, not for any security purpose.
///
/// Deliberately independent of the fuller Python <c>order_state.py</c>/<c>session_manager.py</c>
/// machinery (resume/rehydration, grace-hold, idle sweep -- issue #15, out of scope for #13): this
/// is just the identifier-minting half, scoped to one <see cref="Backend.Sessions.RealtimeProcessor"/>
/// session's lifetime.
/// </summary>
public sealed class SessionIdentifiers
{
    public string SessionToken { get; }
    public int RoundTripIndex { get; private set; }
    public string RoundTripToken => $"{SessionToken}-{RoundTripIndex:D4}";
    public string PersonaId { get; }
    public string ModelId { get; }

    /// <summary>Which pipeline <see cref="ModelId"/> belongs to (Rick's PR #106 review item 3:
    /// order_state.py's <c>model_pipeline</c>, "realtime" | "cascade"; "local" existed here until
    /// #155 dropped it entirely, 2026-09-28). This processor only ever handles the "realtime"
    /// pipeline -- order_state.py's own <c>create_session(model_pipeline=None)</c> default -- so
    /// it is fixed here rather than threaded through as a constructor parameter; a future cascade
    /// C# processor would pass its own pipeline value instead.</summary>
    public string Pipeline => "realtime";

    public SessionIdentifiers(string personaId, string modelId, string? sessionToken = null)
    {
        SessionToken = sessionToken ?? Guid.NewGuid().ToString("n");
        RoundTripIndex = 0;
        PersonaId = personaId;
        ModelId = modelId;
    }

    /// <summary>Advances to the next round trip (called once per non-tool-call response.done) and
    /// returns the identifiers as they stand after advancing.</summary>
    public SessionIdentifiers AdvanceRoundTrip()
    {
        RoundTripIndex++;
        return this;
    }

    /// <summary>Builds the <c>extension.session_metadata</c> / <c>extension.round_trip_token</c>
    /// wire frame -- camelCase keys, matching Python's <c>emit_session_identifiers</c> JSON shape
    /// exactly (the internal snake_case Python attribute names are not what goes over the wire).</summary>
    public JsonObject ToFrame(string eventType) => new()
    {
        ["type"] = eventType,
        ["sessionToken"] = SessionToken,
        ["roundTripIndex"] = RoundTripIndex,
        ["roundTripToken"] = RoundTripToken,
        ["persona"] = PersonaId,
        ["model"] = ModelId,
        ["pipeline"] = Pipeline,
    };
}

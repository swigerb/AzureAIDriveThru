namespace Backend.Sessions;

/// <summary>
/// Seam for wave 7 (#74/#75, ADR-001 decision on per-session persona/model binding): the
/// persona+model-specific pipeline (one of "realtime", "cascade", "local" -- design doc section 7)
/// that actually turns a session's inbound events into outbound browser frames. This wave defines
/// only the shape; SessionActor accepts an optional processor and runs correctly with none bound
/// (events are drained but not acted on) so the one-event-loop-per-session mechanics can be built
/// and tested now, independent of which pipeline eventually plugs in.
/// </summary>
public interface IPipelineProcessor
{
    /// <summary>"realtime" | "cascade" | "local" -- must match one of persona.json's models keys.</summary>
    string PipelineName { get; }

    Task ProcessAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);
}

/// <summary>Base type for whatever a session's mailbox carries -- a client message, an upstream
/// model frame, a control signal, etc. Left as a marker for wave 3+ to extend; the skeleton itself
/// only needs to prove events are processed one at a time, in order, per session.</summary>
public abstract record SessionEvent;

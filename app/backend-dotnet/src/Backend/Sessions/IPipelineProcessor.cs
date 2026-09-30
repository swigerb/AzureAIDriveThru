namespace Backend.Sessions;

/// <summary>
/// Seam for wave 7 (#74/#75, ADR-001 decision on per-session persona/model binding): the
/// persona+model-specific pipeline (one of "realtime", "cascade" -- design doc section 7)
/// that actually turns a session's inbound events into outbound browser frames. This wave defines
/// only the shape; SessionActor accepts an optional processor and runs correctly with none bound
/// (events are drained but not acted on) so the one-event-loop-per-session mechanics can be built
/// and tested now, independent of which pipeline eventually plugs in.
/// </summary>
public interface IPipelineProcessor
{
    /// <summary>"realtime" | "cascade" -- must match one of persona.json's models keys.</summary>
    string PipelineName { get; }

    /// <summary>
    /// Port of processors.py's `PipelineProcessor.resolve_model` (issue #75). Validates
    /// <paramref name="requestedModelId"/> (or, if null, the persona's own default for this
    /// pipeline) against the persona's `allowed` list and the shared model catalog/deployment map,
    /// and returns the concrete model+deployment the session binds to for its whole lifetime.
    /// Called once, before the WebSocket upgrade (Program.cs's `/realtime` handler), by
    /// Models/ModelDispatch.cs's DispatchProcessor after the pipeline itself has already been
    /// chosen from the catalog.
    /// </summary>
    /// <exception cref="Backend.Models.ModelSelectionException">The requested model is unknown,
    /// not allowed for this persona/pipeline, or not deployed.</exception>
    Backend.Models.ResolvedModel ResolveModel(Backend.Personas.Persona persona, string? requestedModelId);

    Task ProcessAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);
}

/// <summary>Base type for whatever a session's mailbox carries -- a client message, an upstream
/// model frame, a control signal, etc. Left as a marker for wave 3+ to extend; the skeleton itself
/// only needs to prove events are processed one at a time, in order, per session.</summary>
public abstract record SessionEvent;

/// <summary>
/// Port of the persona/model/pipeline triple Python's `extension.session_metadata` frame carries
/// (docs/persona-architecture.md section 5.2, issue #74/#75) -- as far as this skeleton reaches
/// this wave: attached to a <see cref="SessionActor"/> at construction time, once persona+model
/// binding has been resolved in Program.cs's `/realtime` handler, so it is available to whatever
/// eventually emits the wire frame (#13). Emitting the frame itself over the WebSocket is out of
/// scope this wave -- SessionActor has no wire-protocol writer yet.
/// </summary>
public sealed record SessionMetadata(string PersonaId, string ModelId, string Pipeline);

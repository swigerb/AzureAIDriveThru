using Backend.Models;
using Backend.Personas;

namespace Backend.Sessions;

/// <summary>
/// Registers the "realtime" pipeline in the processor seam this wave (#12 part 2) so that
/// `/realtime`'s persona+model resolution has a real destination to dispatch to. Model resolution
/// (<see cref="ResolveModel"/>) is the full port of processors.py's `resolve_realtime_model` and is
/// exercised by conformance today; <see cref="ProcessAsync"/> is a deliberate stub -- the actual
/// upstream relay/session-metadata-frame wiring is issue #13's job, not this wave's. Until #13
/// lands, a session bound to this processor drains its mailbox like the unbound case did before
/// this change (see SessionActor.cs), just now with a resolved model attached via
/// SessionMetadata.
/// </summary>
public sealed class RealtimeProcessor : IPipelineProcessor
{
    private readonly ModelCatalog _catalog;
    private readonly string _defaultDeployment;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public RealtimeProcessor(ModelCatalog catalog, string defaultDeployment, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _catalog = catalog;
        _defaultDeployment = defaultDeployment;
        _logger = logger;
    }

    public string PipelineName => "realtime";

    public ResolvedModel ResolveModel(Persona persona, string? requestedModelId) =>
        ModelDispatch.ResolveRealtimeModel(persona, requestedModelId, _catalog, _defaultDeployment, _logger);

    /// <summary>Stub for #13: the real realtime processor owns the upstream Azure OpenAI Realtime
    /// connection and relays frames both directions. This wave only proves the seam dispatches to
    /// it; there is nothing to do with an individual mailbox event yet.</summary>
    public Task ProcessAsync(SessionEvent sessionEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}

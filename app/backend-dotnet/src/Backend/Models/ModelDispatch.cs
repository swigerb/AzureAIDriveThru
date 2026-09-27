using Backend.Personas;
using Microsoft.Extensions.Logging;

namespace Backend.Models;

/// <summary>
/// Port of processors.py's free functions `dispatch_processor` and `resolve_realtime_model`
/// (issue #75, design doc section 7.4 "Dispatch"). Kept as two separate steps, exactly like
/// Python and rtmt.py's `_websocket_handler` call them, because they answer two independent
/// questions in sequence:
///
///   1. <see cref="DispatchProcessor"/>: WHICH pipeline handles this model at all? Resolved
///      purely from the catalog's own <c>pipeline</c> field plus whether that pipeline has a
///      registered <see cref="Backend.Sessions.IPipelineProcessor"/> -- independent of any
///      persona-specific allow-list. A model catalogued for a pipeline with no processor
///      registered (e.g. cascade, before #13 lands its own processor) 404s here, before the
///      persona is even consulted about whether it would have allowed that model.
///   2. <see cref="Backend.Sessions.IPipelineProcessor.ResolveModel"/> (implemented per-pipeline;
///      see <see cref="ResolveRealtimeModel"/> for the realtime pipeline's own rules): now that we
///      know which processor owns this session, is this specific persona+model+deployment
///      combination actually usable?
/// </summary>
public static class ModelDispatch
{
    /// <summary>
    /// Resolves which <see cref="Backend.Sessions.IPipelineProcessor"/> should own this session.
    /// <paramref name="requestedModelId"/> null means "no ?model= query param" -- mirrors
    /// rtmt.py's `_websocket_handler`, which always has SOME model id to dispatch on because the
    /// /realtime endpoint's own default (when ?model= is omitted) is the persona's realtime
    /// pipeline default; an explicit ?model= can still route to a different pipeline's processor.
    /// </summary>
    /// <exception cref="ModelSelectionException">The effective model id isn't catalogued at all,
    /// or its pipeline has no registered processor.</exception>
    public static (string PipelineName, Backend.Sessions.IPipelineProcessor Processor) DispatchProcessor(
        Persona persona,
        string? requestedModelId,
        ModelCatalog catalog,
        Backend.Sessions.ProcessorRegistry registry)
    {
        var effectiveModelId = requestedModelId ?? persona.Models.Realtime.Default;

        ModelEntry entry;
        try
        {
            entry = catalog.Get(effectiveModelId);
        }
        catch (KeyNotFoundException)
        {
            throw new ModelSelectionException($"Unknown or disallowed model: {Repr(requestedModelId)}");
        }

        var processor = registry.Get(entry.Pipeline);
        if (processor is null)
        {
            throw new ModelSelectionException(
                $"Unknown or disallowed model: {Repr(requestedModelId)} (pipeline '{entry.Pipeline}' has no registered processor).");
        }

        return (entry.Pipeline, processor);
    }

    /// <summary>
    /// Port of processors.py's `resolve_realtime_model` -- the realtime pipeline's own
    /// <see cref="Backend.Sessions.IPipelineProcessor.ResolveModel"/> rule set:
    ///  - omitted -&gt; persona's realtime default;
    ///  - must equal the persona's own default OR appear in its realtime `allowed` list;
    ///  - must be catalogued for exactly the realtime pipeline;
    ///  - must have an AZURE_AI_MODEL_DEPLOYMENTS entry, EXCEPT the persona's own default, which
    ///    falls back to <paramref name="defaultDeployment"/> (today's single, pre-#75
    ///    AZURE_OPENAI_REALTIME_DEPLOYMENT env var) with a logged warning -- back-compat for
    ///    decks that haven't populated the new deployment map yet. No other model gets this
    ///    fallback.
    /// </summary>
    /// <exception cref="ModelSelectionException">Not allowed for this persona, not catalogued for
    /// realtime, or (non-default) not deployed.</exception>
    public static ResolvedModel ResolveRealtimeModel(
        Persona persona,
        string? requestedModelId,
        ModelCatalog catalog,
        string defaultDeployment,
        ILogger? logger = null)
    {
        var pipelineCfg = persona.Models.Realtime;
        var modelId = requestedModelId ?? pipelineCfg.Default;

        if (modelId != pipelineCfg.Default && !pipelineCfg.Allowed.Contains(modelId))
        {
            throw new ModelSelectionException(
                $"Model '{modelId}' is not allowed for persona '{persona.Id}''s realtime pipeline.");
        }

        if (!catalog.IsCataloguedFor(modelId, "realtime"))
        {
            throw new ModelSelectionException($"Model '{modelId}' is not catalogued for the realtime pipeline.");
        }

        var deployment = catalog.DeploymentFor(modelId);
        if (deployment is null)
        {
            if (modelId != pipelineCfg.Default)
            {
                throw new ModelSelectionException(
                    $"Model '{modelId}' is catalogued but not deployed (no AZURE_AI_MODEL_DEPLOYMENTS entry).");
            }

            deployment = defaultDeployment;
            logger?.LogWarning(
                "Persona {PersonaId}'s realtime default model {ModelId} has no AZURE_AI_MODEL_DEPLOYMENTS entry -- " +
                "falling back to AZURE_OPENAI_REALTIME_DEPLOYMENT ({Deployment}) for back-compat.",
                persona.Id, modelId, defaultDeployment);
        }

        var entry = catalog.Get(modelId);
        return new ResolvedModel(modelId, "realtime", deployment, entry.Reasoning);
    }

    private static string Repr(string? value) => value is null ? "null" : $"'{value}'";
}

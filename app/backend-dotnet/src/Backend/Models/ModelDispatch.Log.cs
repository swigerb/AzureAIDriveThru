namespace Backend.Models;

/// <summary>
/// Source-generated <see cref="ILogger"/> extension methods for <see cref="ModelDispatch"/>,
/// avoiding CA1848 allocations. EventIds 11100-11199 are reserved for this file; message
/// templates, levels and placeholder names are copied verbatim from the call sites they replace
/// -- the conformance suite matches on log text, so none of that may change.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 11100, Level = LogLevel.Warning, Message = "Persona {PersonaId}'s realtime default model {ModelId} has no AZURE_AI_MODEL_DEPLOYMENTS entry -- falling back to AZURE_OPENAI_REALTIME_DEPLOYMENT ({Deployment}) for back-compat.")]
    public static partial void RealtimeDefaultDeploymentFallback(this ILogger logger, string personaId, string modelId, string deployment);
}

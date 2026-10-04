namespace Backend.Models;

/// <summary>Port of app/backend/model_catalog.py's ModelEntry dataclass: one config.yaml
/// models.catalog row (design doc section 7.2). "reasoning" always defaults false rather than
/// null -- there is no "unknown" state, a catalog entry either declares it or doesn't (Rick's PR
/// #106 review item 1: reasoning always comes from the catalog entry, never a heuristic).</summary>
public sealed record ModelEntry(
    string Id,
    string Pipeline,
    string Label,
    bool Reasoning = false,
    bool? ToolCalling = null);

/// <summary>
/// Port of model_catalog.py's <c>CascadeAudioConfig</c> (issue #82, design doc section 7.2):
/// config.yaml's <c>models.cascade</c> -- the catalog ids for the cascade pipeline's own
/// transcription (speech-to-text) and TTS (text-to-speech) models.
///
/// These are NOT <c>models.catalog</c> rows -- they're not user-selectable chat models, just the
/// two fixed audio deployments every cascade session uses regardless of which cascade *chat*
/// model (a <see cref="ModelEntry"/> with <c>Pipeline == "cascade"</c>) the session is bound to.
/// Their actual Foundry deployment names still come from the SAME <c>AZURE_AI_MODEL_DEPLOYMENTS</c>
/// map as chat models (<see cref="ModelCatalog.DeploymentFor"/>) -- there is no separate audio
/// deployment map.
/// </summary>
public sealed record CascadeAudioConfig(string Transcription, string Tts);

/// <summary>Raised when config.yaml's models.catalog or the AZURE_AI_MODEL_DEPLOYMENTS env var is
/// malformed, or an enabled persona's own pipeline default isn't catalogued for that pipeline
/// (ModelCatalog.ValidatePersonaDefaults). Always names the offending entry/field, mirroring
/// model_catalog.py's ModelValidationError -- same fail-fast style as
/// PersonaValidationException.</summary>
public sealed class ModelValidationException : Exception
{
    public ModelValidationException(string message) : base(message)
    {
    }

    public ModelValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

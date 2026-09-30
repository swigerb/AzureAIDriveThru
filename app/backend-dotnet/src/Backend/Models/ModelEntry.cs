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

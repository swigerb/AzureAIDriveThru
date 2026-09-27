namespace Backend.Models;

/// <summary>
/// Port of processors.py's ResolvedModel frozen dataclass: the outcome of validating a session's
/// requested (or defaulted) model id against its persona's allow-list and the shared
/// catalog/deployment map -- design doc section 7.3: "Selectable = catalog ∩ deployment ∩
/// persona-allowed."
///
/// Rick's PR #106 review item 1: Reasoning is ALWAYS the catalog entry's own reasoning flag -- for
/// every model, including the persona's own pipeline default. There is no default-path special
/// case; design doc's "reasoning sent only for catalog reasoning models" (section 7.5).
/// </summary>
public sealed record ResolvedModel(string Id, string Pipeline, string Deployment, bool Reasoning);

/// <summary>
/// Port of processors.py's ModelSelectionError: raised when a requested model isn't selectable for
/// a persona/pipeline/deployment combination -- unknown, not in the persona's own allowed list,
/// catalogued for a different pipeline (the processor-seam guard), or catalogued but not (yet)
/// deployed. The caller (Program.cs's `/realtime` handler) turns this into the same plain HTTP 404
/// an unknown/disabled persona already gets -- never a silent fallback to some other model.
/// </summary>
public sealed class ModelSelectionException : Exception
{
    public ModelSelectionException(string message) : base(message)
    {
    }

    public ModelSelectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

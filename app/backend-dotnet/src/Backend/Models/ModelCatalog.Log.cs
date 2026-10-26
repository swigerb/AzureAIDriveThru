namespace Backend.Models;

/// <summary>
/// Source-generated <see cref="ILogger"/> extension methods for <see cref="ModelCatalog"/>,
/// avoiding CA1848 allocations. EventIds 11000-11099 are reserved for this file; message
/// templates, levels and placeholder names are copied verbatim from the call sites they replace
/// -- the conformance suite matches on log text, so none of that may change.
/// </summary>
internal static partial class Log
{
    // The template deliberately names {Pipeline} twice; both occurrences render the same value,
    // exactly like the original LogWarning call that passed the pipeline name twice.
    [LoggerMessage(EventId = 11000, Level = LogLevel.Warning, Message = "Persona {PersonaId}'s {Pipeline} allowed model {AllowedId} is not in config.yaml's models.catalog for pipeline {Pipeline} -- it will 404 if a guest ever requests it explicitly by id.")]
    public static partial void AllowedModelNotCatalogued(this ILogger logger, string personaId, string pipeline, string allowedId);
}

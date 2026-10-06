using Backend.Personas;

namespace Backend.Prompts;

/// <summary>
/// Loads and caches one <see cref="PromptLoader"/> per enabled persona at startup so every
/// process-wide service that needs prompt content reads the same validated loader instance.
/// </summary>
internal sealed class PromptLoaderRegistry
{
    private readonly IReadOnlyDictionary<string, PromptLoader> _loaders;

    public PromptLoaderRegistry(string personasDir, PersonaCatalog personaCatalog)
    {
        var loaders = new Dictionary<string, PromptLoader>(StringComparer.Ordinal)
        {
            [personaCatalog.DefaultPersonaId] = new PromptLoader(personasDir, personaCatalog.DefaultPersonaId),
        };
        var allToolNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var personaId in personaCatalog.Ids)
        {
            if (!loaders.TryGetValue(personaId, out var loader))
            {
                loader = new PromptLoader(personasDir, personaId);
                loaders[personaId] = loader;
            }

            foreach (var schema in loader.ToolSchemas)
            {
                if (schema.TryGetValue("name", out var nameObj) && nameObj?.ToString() is { Length: > 0 } toolName)
                {
                    allToolNames.Add(toolName);
                }
            }
        }

        _loaders = loaders;
        AllToolNames = allToolNames;
    }

    public IReadOnlyDictionary<string, PromptLoader> Loaders => _loaders;

    public IReadOnlySet<string> AllToolNames { get; }

    public PromptLoader GetRequired(string personaId) =>
        _loaders.TryGetValue(personaId, out var loader)
            ? loader
            : throw new KeyNotFoundException($"No prompt loader is registered for persona '{personaId}'.");
}

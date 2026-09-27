using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Backend.Personas;

/// <summary>
/// Port of app/backend/persona_loader.py's PersonaCatalog (docs/dotnet_mapping.md). Discovers,
/// parses, and validates every enabled persona pack under PERSONAS_DIR at process startup, and
/// refuses to start (throws PersonaValidationException, uncaught out of Program.cs) on the first
/// invalid one (ADR-001 decision 1/2: "one contract, two backends", "fail fast on an invalid
/// pack"). Session/model binding to a specific persona is wave 7 (#74/#75) -- this catalog is
/// just the seam later waves bind against.
/// </summary>
public sealed class PersonaCatalog
{
    private readonly IReadOnlyDictionary<string, Persona> _personas;

    private PersonaCatalog(IReadOnlyDictionary<string, Persona> personas, string defaultPersonaId)
    {
        _personas = personas;
        DefaultPersonaId = defaultPersonaId;
    }

    public string DefaultPersonaId { get; }
    public Persona Default => _personas[DefaultPersonaId];
    public IReadOnlyCollection<string> Ids => (IReadOnlyCollection<string>)_personas.Keys;

    public bool Contains(string id) => _personas.ContainsKey(id);

    public Persona Get(string id) =>
        _personas.TryGetValue(id, out var persona)
            ? persona
            : throw new KeyNotFoundException($"Unknown persona '{id}'. Enabled personas: {string.Join(", ", Ids)}.");

    /// <summary>
    /// Env-var-driven, exactly like Python's PersonaCatalog.load(): PERSONAS_DIR (default
    /// &lt;repo&gt;/personas, /app/personas in a container), PERSONAS (comma list; default: every
    /// folder under PERSONAS_DIR with a persona.json), DEFAULT_PERSONA (must be one of the enabled
    /// ids; defaults to "sonic" if enabled, else the first enabled id alphabetically).
    /// </summary>
    /// <exception cref="PersonaValidationException">The directory, an enabled pack, or the
    /// PERSONAS/DEFAULT_PERSONA env vars are invalid.</exception>
    public static PersonaCatalog Load(
        string? personasDir = null,
        string? personasEnv = null,
        string? defaultPersonaEnv = null)
    {
        var baseDir = personasDir
            ?? Environment.GetEnvironmentVariable("PERSONAS_DIR")
            ?? Path.Combine(RepoRootLocator.Find(), "personas");

        if (!Directory.Exists(baseDir))
        {
            throw new PersonaValidationException($"PERSONAS_DIR '{baseDir}' does not exist or is not a directory.");
        }

        var personaSchema = LoadJsonSchema(Path.Combine(baseDir, "persona.schema.json"));
        var menuSchema = LoadJsonSchema(Path.Combine(baseDir, "menu.schema.json"));

        var discovered = Directory.EnumerateDirectories(baseDir)
            .Select(d => Path.GetFileName(d)!)
            .Where(name => File.Exists(Path.Combine(baseDir, name, "persona.json")))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var personasCsv = personasEnv ?? Environment.GetEnvironmentVariable("PERSONAS");
        var enabledIds = string.IsNullOrWhiteSpace(personasCsv)
            ? discovered
            : personasCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToList();

        if (enabledIds.Count == 0)
        {
            throw new PersonaValidationException(
                $"No personas enabled. Checked PERSONAS_DIR '{baseDir}' -- expected at least one " +
                "subdirectory containing a persona.json.");
        }

        var personas = new Dictionary<string, Persona>(StringComparer.Ordinal);
        foreach (var id in enabledIds)
        {
            if (!discovered.Contains(id))
            {
                throw new PersonaValidationException(
                    $"PERSONAS lists '{id}', but no persona.json was found at " +
                    $"'{Path.Combine(baseDir, id, "persona.json")}'.");
            }

            personas[id] = LoadOnePersona(baseDir, id, personaSchema, menuSchema);
        }

        var defaultId = defaultPersonaEnv ?? Environment.GetEnvironmentVariable("DEFAULT_PERSONA");
        if (defaultId is not null)
        {
            if (!personas.ContainsKey(defaultId))
            {
                throw new PersonaValidationException(
                    $"DEFAULT_PERSONA '{defaultId}' is not in the enabled persona list " +
                    $"({string.Join(", ", personas.Keys)}).");
            }
        }
        else
        {
            defaultId = personas.ContainsKey("sonic")
                ? "sonic"
                : personas.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        }

        return new PersonaCatalog(personas, defaultId);
    }

    private static JsonSchema LoadJsonSchema(string path)
    {
        if (!File.Exists(path))
        {
            throw new PersonaValidationException($"Required schema file not found: {path}");
        }

        try
        {
            return JsonSchema.FromFile(path);
        }
        catch (JsonException exc)
        {
            throw new PersonaValidationException($"Malformed JSON in schema file {path}: {exc.Message}");
        }
    }

    private static Persona LoadOnePersona(
        string baseDir, string personaId, JsonSchema personaSchema, JsonSchema menuSchema)
    {
        var packDir = Path.Combine(baseDir, personaId);
        var manifestPath = Path.Combine(packDir, "persona.json");

        if (!File.Exists(manifestPath))
        {
            throw new PersonaValidationException(
                $"Persona '{personaId}' is enabled but {manifestPath} does not exist.");
        }

        var manifestNode = LoadJsonFile(manifestPath, personaId);

        // Layer 1: validate against the shared JSON Schema (the same contract Python and CI
        // validate against -- design doc section 4.4).
        PersonaSchemaValidator.Validate(personaSchema, manifestNode, personaId, "persona.json");

        // Layer 2: parse into the typed, JsonUnmappedMemberHandling.Disallow record (belt and
        // suspenders against schema/model drift; see PersonaSchemaValidator's own doc comment).
        Persona manifest;
        try
        {
            manifest = manifestNode.Deserialize<Persona>(PersonaJsonOptions.Value)
                ?? throw new PersonaValidationException(
                    $"Persona '{personaId}': {manifestPath} deserialized to null.");
        }
        catch (JsonException exc)
        {
            throw new PersonaValidationException(
                $"Persona '{personaId}': {manifestPath} failed model validation: {exc.Message}");
        }

        if (manifest.Id != personaId)
        {
            throw new PersonaValidationException(
                $"Persona '{personaId}': {manifestPath} declares id '{manifest.Id}', which does " +
                $"not match its folder name '{personaId}'.");
        }

        // Validate the pack's menu file too, so a broken menu also fails startup rather than
        // surfacing later as a runtime lookup failure.
        var menuPath = Path.Combine(packDir, "menu", "menuItems.json");
        if (!File.Exists(menuPath))
        {
            throw new PersonaValidationException($"Persona '{personaId}': menu file not found at {menuPath}.");
        }

        var menuNode = LoadJsonFile(menuPath, personaId);
        PersonaSchemaValidator.Validate(menuSchema, menuNode, personaId, "menu/menuItems.json");

        PersonaMenu menu;
        try
        {
            menu = menuNode.Deserialize<PersonaMenu>(PersonaJsonOptions.Value)
                ?? throw new PersonaValidationException(
                    $"Persona '{personaId}': {menuPath} deserialized to null.");
        }
        catch (JsonException exc)
        {
            throw new PersonaValidationException(
                $"Persona '{personaId}': {menuPath} failed model validation: {exc.Message}");
        }

        var promptsDir = Path.Combine(packDir, "prompts");
        if (!Directory.Exists(promptsDir))
        {
            throw new PersonaValidationException(
                $"Persona '{personaId}': prompts directory not found at {promptsDir}.");
        }

        return manifest with
        {
            Menu = menu,
            PackDir = packDir,
            AssetsDir = Path.Combine(packDir, "assets"),
            MenuPath = menuPath,
            PromptsDir = promptsDir,
        };
    }

    private static JsonNode LoadJsonFile(string path, string personaId)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException exc)
        {
            throw new PersonaValidationException($"Persona '{personaId}': could not read {path}: {exc.Message}");
        }

        try
        {
            return JsonNode.Parse(text) ?? throw new PersonaValidationException(
                $"Persona '{personaId}': Malformed JSON in {path}: parsed to null.");
        }
        catch (JsonException exc)
        {
            throw new PersonaValidationException($"Persona '{personaId}': Malformed JSON in {path}: {exc.Message}");
        }
    }
}

internal static class PersonaJsonOptions
{
    public static readonly JsonSerializerOptions Value = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

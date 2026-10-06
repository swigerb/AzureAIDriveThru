using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Personas;

namespace Backend.Models;

/// <summary>
/// Port of app/backend/model_catalog.py's ModelCatalog (issue #75, design doc section 7.2). Model
/// flexibility has three layers, each owned by a different piece of config:
///
/// 1. **Catalog** (this class; config.yaml's top-level <c>models: catalog:</c> list): the facts
///    about a model that are the same on every deployment -- its id, which pipeline it belongs to
///    (realtime | cascade), a display label, and its capabilities (reasoning for
///    realtime/cascade, toolCalling for cascade).
/// 2. **Deployment** (this class; the AZURE_AI_MODEL_DEPLOYMENTS env var, a JSON map of catalog id
///    -&gt; Foundry deployment name): which of the catalogued models actually exist on THIS
///    deployment. A catalog entry with no deployment mapped for it is not selectable (section
///    7.3).
/// 3. **Persona** (Personas/PersonaModels.cs's PersonaModelsBlock/PersonaModelPipeline, issue #74):
///    which catalogued models a persona pack allows per pipeline, and its own default. Unaffected
///    by this class.
///
/// Design doc section 7.3: "Selectable = catalog ∩ deployment ∩ persona-allowed." This class owns
/// the first two terms of that intersection; Models/ModelDispatch.cs combines all three.
///
/// Rick's PR #106 review item 1 (ported here byte-for-byte from model_catalog.py): there is NO
/// default-path special case -- every model a persona can bind to, including its own pipeline
/// default, resolves through the catalog exactly like any other requested id. The ONLY back-compat
/// carve-out is on the *deployment* term for the realtime pipeline's default (see
/// Models/ModelDispatch.cs's ResolveRealtimeModel), never here.
/// </summary>
internal sealed class ModelCatalog
{
    private static readonly HashSet<string> Pipelines = ["realtime", "cascade"];
    private static readonly HashSet<string> RequiredEntryFields = ["id", "pipeline", "label"];
    private static readonly HashSet<string> KnownEntryFields =
        ["id", "pipeline", "label", "reasoning", "toolCalling"];

    private const string DeploymentsEnvVar = "AZURE_AI_MODEL_DEPLOYMENTS";

    private readonly IReadOnlyDictionary<string, ModelEntry> _entries;
    private readonly IReadOnlyDictionary<string, string> _deployments;
    private readonly CascadeAudioConfig? _cascadeAudio;

    private ModelCatalog(IReadOnlyDictionary<string, ModelEntry> entries, IReadOnlyDictionary<string, string> deployments,
        CascadeAudioConfig? cascadeAudio = null)
    {
        _entries = entries;
        _deployments = deployments;
        _cascadeAudio = cascadeAudio;
    }

    /// <summary>Every catalogued model id, ordinal sorted (mirrors Python's `sorted(self._entries)`).</summary>
    public IReadOnlyList<string> Ids => _entries.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();

    public bool Contains(string modelId) => _entries.ContainsKey(modelId);

    public ModelEntry Get(string modelId) =>
        _entries.TryGetValue(modelId, out var entry)
            ? entry
            : throw new KeyNotFoundException(
                $"Model '{modelId}' is not in the catalog. Known models: {DescribeIds()}.");

    /// <summary>The Foundry deployment name for <paramref name="modelId"/>, or null if
    /// AZURE_AI_MODEL_DEPLOYMENTS doesn't map it yet. Does NOT apply the realtime-pipeline-default
    /// back-compat rule -- see Models/ModelDispatch.cs's ResolveRealtimeModel.</summary>
    public string? DeploymentFor(string modelId) => _deployments.GetValueOrDefault(modelId);

    public bool IsDeployed(string modelId) => DeploymentFor(modelId) is not null;

    /// <summary>True iff <paramref name="modelId"/> is catalogued at all AND catalogued for
    /// exactly <paramref name="pipeline"/> (the processor-seam guard). Deliberately does NOT check
    /// deployment -- see <see cref="IsSelectable"/> for the full catalog ∩ deployment check.</summary>
    public bool IsCataloguedFor(string modelId, string pipeline) =>
        _entries.TryGetValue(modelId, out var entry) && entry.Pipeline == pipeline;

    /// <summary>True iff catalogued for exactly <paramref name="pipeline"/> AND has a deployment
    /// mapped (design doc section 7.3's catalog ∩ deployment). Persona-allowed is the caller's own
    /// job. Rick's PR #106 review item 1: no default-path exception here -- a pipeline default
    /// that only reaches its deployment via the AZURE_OPENAI_REALTIME_DEPLOYMENT back-compat
    /// fallback is still NOT selectable by this definition, so /api/personas/{id}'s picker never
    /// offers a model that would 404 if explicitly requested by id.</summary>
    public bool IsSelectable(string modelId, string pipeline) => IsCataloguedFor(modelId, pipeline) && IsDeployed(modelId);

    /// <summary>config.yaml's `models.cascade`'s transcription/tts catalog ids (issue #82), or
    /// null if config.yaml doesn't declare one -- e.g. a deployment with the cascade pipeline
    /// unregistered, or a test fixture catalog that doesn't need it. <see cref="Sessions.CascadeProcessor"/>
    /// resolves each id's actual deployment name via <see cref="DeploymentFor"/>, same as any chat
    /// model.</summary>
    public CascadeAudioConfig? CascadeAudio => _cascadeAudio;


    /// <summary>
    /// Rick's PR #106 review item 1: startup fails if any enabled persona's own pipeline default
    /// model isn't in the catalog for that pipeline -- an unusable default should stop startup,
    /// not surface as a confusing 404 on the first ?model=-omitted request. Only the DEFAULT is
    /// fail-fast; a non-default `allowed` id that isn't catalogued only logs a warning, since it
    /// only 404s if a guest actually asks for it by id (see Models/ModelDispatch.cs).
    /// </summary>
    /// <exception cref="ModelValidationException">An enabled persona's pipeline default isn't
    /// catalogued for that pipeline.</exception>
    public void ValidatePersonaDefaults(PersonaCatalog personaCatalog, ILogger<ModelCatalog> logger)
    {
        foreach (var personaId in personaCatalog.Ids)
        {
            var persona = personaCatalog.Get(personaId);
            foreach (var (pipelineName, pipelineCfg) in PersonaPipelines(persona))
            {
                if (pipelineCfg is null)
                {
                    continue;
                }

                if (!IsCataloguedFor(pipelineCfg.Default, pipelineName))
                {
                    throw new ModelValidationException(
                        $"Persona '{personaId}''s {pipelineName} default model '{pipelineCfg.Default}' is not in " +
                        $"config.yaml's models.catalog for pipeline '{pipelineName}'. Known models: {DescribeIds()}.");
                }

                foreach (var allowedId in pipelineCfg.Allowed)
                {
                    if (allowedId == pipelineCfg.Default)
                    {
                        continue;
                    }
                    if (!IsCataloguedFor(allowedId, pipelineName))
                    {
                        logger.LogWarning(
                            "Persona {PersonaId}'s {Pipeline} allowed model {AllowedId} is not in config.yaml's " +
                            "models.catalog for pipeline {Pipeline} -- it will 404 if a guest ever requests it " +
                            "explicitly by id.",
                            personaId, pipelineName, allowedId, pipelineName);
                    }
                }
            }
        }
    }

    private static IEnumerable<(string PipelineName, PersonaModelPipeline? PipelineCfg)> PersonaPipelines(Persona persona)
    {
        yield return ("realtime", persona.Models.Realtime);
        yield return ("cascade", persona.Models.Cascade);
    }

    private string DescribeIds() => Ids.Count > 0 ? string.Join(", ", Ids) : "(none)";

    /// <summary>
    /// Loads config.yaml's top-level `models: catalog:` list plus the AZURE_AI_MODEL_DEPLOYMENTS
    /// env var (mirrors model_catalog.py's `ModelCatalog.load`). An absent `models` section (or an
    /// absent/empty `catalog` list within it) is a valid, empty catalog -- not an error -- since
    /// nothing downstream is fail-fast on the catalog itself being non-empty (only on an enabled
    /// persona's own default not being catalogued, via <see cref="ValidatePersonaDefaults"/>).
    /// </summary>
    /// <exception cref="ModelValidationException">Malformed models.catalog or
    /// AZURE_AI_MODEL_DEPLOYMENTS.</exception>
    public static ModelCatalog FromConfig(AppConfig config, IReadOnlyDictionary<string, string>? environment = null)
    {
        var modelsSection = config.TryGetSection("models");
        var rawCatalog = new List<object>();
        if (modelsSection is not null && modelsSection.TryGetValue("catalog", out var catalogRaw) && catalogRaw is not null)
        {
            if (catalogRaw is not IEnumerable<object> list)
            {
                throw new ModelValidationException($"config.yaml models.catalog must be a list, got {DescribeType(catalogRaw)}.");
            }
            rawCatalog = list.ToList();
        }

        var entries = new Dictionary<string, ModelEntry>(StringComparer.Ordinal);
        for (var index = 0; index < rawCatalog.Count; index++)
        {
            var entry = ParseEntry(rawCatalog[index], index);
            if (!entries.TryAdd(entry.Id, entry))
            {
                throw new ModelValidationException(
                    $"config.yaml models.catalog has a duplicate model id '{entry.Id}' (entry {index}).");
            }
        }

        string? deploymentsRaw;
        if (environment is not null)
        {
            environment.TryGetValue(DeploymentsEnvVar, out deploymentsRaw);
        }
        else
        {
            deploymentsRaw = BackendEnvironment.Get(DeploymentsEnvVar);
        }

        var deployments = ParseDeploymentMap(deploymentsRaw);
        object? cascadeRaw = null;
        modelsSection?.TryGetValue("cascade", out cascadeRaw);
        var cascadeAudio = ParseCascadeAudioConfig(cascadeRaw);
        return new ModelCatalog(entries, deployments, cascadeAudio);
    }

    /// <summary>
    /// Port of model_catalog.py's `_parse_cascade_audio_config`: config.yaml's `models.cascade`
    /// is optional (a deployment that hasn't registered the cascade pipeline yet simply omits it,
    /// yielding a null config -- not an error), but if present must be a mapping with exactly the
    /// two required fields, each a non-empty string.
    /// </summary>
    /// <exception cref="ModelValidationException">`models.cascade` is present but malformed.</exception>
    private static CascadeAudioConfig? ParseCascadeAudioConfig(object? raw)
    {
        if (raw is null)
        {
            return null;
        }
        if (raw is not IDictionary<object, object> map)
        {
            throw new ModelValidationException($"config.yaml models.cascade must be a mapping, got {DescribeType(raw)}.");
        }

        var dict = map.ToDictionary(kv => kv.Key.ToString()!, kv => kv.Value);
        var known = new HashSet<string> { "transcription", "tts" };

        var unknown = dict.Keys.Where(k => !known.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ModelValidationException($"config.yaml models.cascade has unknown field(s): {FormatList(unknown)}.");
        }

        var missing = known.Where(f => !dict.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new ModelValidationException($"config.yaml models.cascade is missing required field(s): {FormatList(missing)}.");
        }

        if (dict["transcription"] is not string transcription || string.IsNullOrWhiteSpace(transcription))
        {
            throw new ModelValidationException("config.yaml models.cascade's 'transcription' must be a non-empty string.");
        }
        if (dict["tts"] is not string tts || string.IsNullOrWhiteSpace(tts))
        {
            throw new ModelValidationException("config.yaml models.cascade's 'tts' must be a non-empty string.");
        }

        return new CascadeAudioConfig(transcription, tts);
    }


    private static ModelEntry ParseEntry(object raw, int index)
    {
        if (raw is not IDictionary<object, object> map)
        {
            throw new ModelValidationException($"config.yaml models.catalog[{index}] must be a mapping, got {DescribeType(raw)}.");
        }

        var dict = map.ToDictionary(kv => kv.Key.ToString()!, kv => kv.Value);

        var unknown = dict.Keys.Where(k => !KnownEntryFields.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ModelValidationException(
                $"config.yaml models.catalog[{index}] has unknown field(s): {FormatList(unknown)}.");
        }

        var missing = RequiredEntryFields.Where(f => !dict.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new ModelValidationException(
                $"config.yaml models.catalog[{index}] is missing required field(s): {FormatList(missing)}.");
        }

        if (dict["id"] is not string id || string.IsNullOrWhiteSpace(id))
        {
            throw new ModelValidationException($"config.yaml models.catalog[{index}]'s 'id' must be a non-empty string.");
        }

        var pipeline = dict["pipeline"]?.ToString();
        if (pipeline is null || !Pipelines.Contains(pipeline))
        {
            throw new ModelValidationException(
                $"config.yaml models.catalog[{index}] ('{id}') has unknown pipeline '{pipeline}'; " +
                $"expected one of {FormatList(Pipelines.OrderBy(p => p, StringComparer.Ordinal))}.");
        }

        if (dict["label"] is not string label || string.IsNullOrWhiteSpace(label))
        {
            throw new ModelValidationException($"config.yaml models.catalog[{index}] ('{id}')'s 'label' must be a non-empty string.");
        }

        var reasoning = false;
        if (dict.TryGetValue("reasoning", out var reasoningRaw) && reasoningRaw is not null)
        {
            if (!TryParseBool(reasoningRaw, out var reasoningBool))
            {
                throw new ModelValidationException($"config.yaml models.catalog[{index}] ('{id}')'s 'reasoning' must be a bool.");
            }
            reasoning = reasoningBool;
        }

        bool? toolCalling = null;
        if (dict.TryGetValue("toolCalling", out var toolCallingRaw) && toolCallingRaw is not null)
        {
            if (!TryParseBool(toolCallingRaw, out var toolCallingBool))
            {
                throw new ModelValidationException($"config.yaml models.catalog[{index}] ('{id}')'s 'toolCalling' must be a bool.");
            }
            toolCalling = toolCallingBool;
        }

        return new ModelEntry(id, pipeline, label, reasoning, toolCalling);
    }

    /// <summary>
    /// YamlDotNet's untyped `Deserialize&lt;object?&gt;()` returns every scalar as a plain string
    /// (see Configuration/SecurityConfig.cs's own doc comment for the same gotcha) -- so
    /// `reasoning: true` in config.yaml comes back as the string "True"/"true", not the bool
    /// `true`. Tolerates both shapes for the same reason SecurityConfig.ParseBool does.
    /// </summary>
    private static bool TryParseBool(object value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case string s when bool.TryParse(s, out var parsed):
                result = parsed;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseDeploymentMap(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException exc)
        {
            throw new ModelValidationException($"{DeploymentsEnvVar} is not valid JSON: {exc.Message}");
        }

        if (parsed is not JsonObject obj)
        {
            throw new ModelValidationException(
                $"{DeploymentsEnvVar} must be a JSON object (catalog id -> deployment name), got {DescribeJson(parsed)}.");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in obj)
        {
            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var strValue) || string.IsNullOrWhiteSpace(strValue))
            {
                throw new ModelValidationException(
                    $"{DeploymentsEnvVar} entry '{key}' must map a string catalog id to a non-empty string deployment name.");
            }
            map[key] = strValue;
        }
        return map;
    }

    private static string DescribeType(object? value) => value switch
    {
        null => "null",
        string => "a scalar string",
        IEnumerable<object> => "a sequence",
        _ => value.GetType().Name,
    };

    private static string DescribeJson(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "an array",
        JsonValue => "a scalar",
        _ => node.GetType().Name,
    };

    private static string FormatList(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";
}

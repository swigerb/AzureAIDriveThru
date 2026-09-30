using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Backend.Prompts;

/// <summary>Raised for any prompt-loading problem -- missing prompts directory, missing/malformed
/// YAML file, or an empty/invalid assembled prompt. Mirrors app/backend/prompt_loader.py's
/// FileNotFoundError/ValueError, thrown out of Program.cs startup uncaught (fail-fast, matching
/// personas and config).</summary>
public sealed class PromptLoadException(string message) : Exception(message);

/// <summary>
/// Port of app/backend/prompt_loader.py (docs/dotnet_mapping.md), minus DEV_MODE hot-reload
/// (design doc section 4.4's "how each backend loads a pack" table marks this explicitly NOT
/// required in C#). Loads and validates the five prompt YAML files for one persona's
/// personas/&lt;id&gt;/prompts/ directory, fail-fast on any problem. #14 adds the rendering half
/// (RenderError/RenderTemplate/GetDeltaTemplate/GetUpsellHint) needed once real order/tool
/// processing exists -- a lightweight single-pass "{{var}}" substitution rather than embedding a
/// full Jinja2-equivalent engine, since every real persona pack's templates (verified against
/// the default pack) only ever use plain variable interpolation, never control flow.
/// </summary>
public sealed class PromptLoader
{
    private readonly IDeserializer _yaml = new DeserializerBuilder().Build();

    /// <summary>Required rejection-message keys (#125, fail-fast follow-up to #116): every
    /// structured rejection app/backend/tools.py's update_order/modify path can return renders one
    /// of these keys via PromptLoader.render_error (Python), plus generic_error as the shared
    /// fallback. Mirrors app/backend/prompt_loader.py's REQUIRED_ERROR_MESSAGE_KEYS byte-for-byte
    /// (same values, same order) -- Backend.Tests's
    /// PromptLoaderRequiredErrorKeysTests.RequiredErrorMessageKeys_MatchPython parses that Python
    /// file and asserts the two lists are equal, so the two can never silently drift apart. See
    /// docs/persona-architecture.md section 6.</summary>
    public static readonly IReadOnlyList<string> RequiredErrorMessageKeys =
    [
        "generic_error",
        "item_not_on_menu",
        "size_not_available",
        "item_not_in_order",
        "machine_unavailable",
        "extras_blocked_category",
        "extras_no_base_item",
    ];

    public string PersonaId { get; }
    public string SystemPrompt { get; }
    public IReadOnlyDictionary<object, object> Greeting { get; }
    public IReadOnlyList<IReadOnlyDictionary<object, object>> ToolSchemas { get; }
    public IReadOnlyDictionary<object, object> ErrorMessages { get; }
    public IReadOnlyDictionary<object, object> Hints { get; }

    public PromptLoader(string personasDir, string personaId)
    {
        PersonaId = personaId;
        var promptsDir = Path.Combine(personasDir, personaId, "prompts");
        if (!Directory.Exists(promptsDir))
        {
            throw new PromptLoadException(
                $"Prompt directory not found: {promptsDir}. Expected prompts at " +
                $"personas/{personaId}/prompts/ (PERSONAS_DIR={personasDir}).");
        }

        var systemPromptData = LoadYaml(promptsDir, "system_prompt.yaml");
        SystemPrompt = AssembleSystemPrompt(systemPromptData);

        var greetingData = LoadYaml(promptsDir, "greeting.yaml");
        Greeting = ValidateGreeting(greetingData);

        var toolSchemasData = LoadYaml(promptsDir, "tool_schemas.yaml");
        ToolSchemas = ValidateToolSchemas(toolSchemasData);

        var errorMessagesData = LoadYaml(promptsDir, "error_messages.yaml");
        ErrorMessages = (IReadOnlyDictionary<object, object>?)GetMapping(errorMessagesData, "messages")
            ?? new Dictionary<object, object>();
        ValidateErrorMessages(ErrorMessages);

        Hints = LoadYaml(promptsDir, "hints.yaml");
    }

    // Matches a Jinja2-style "{{ variable }}" token. Every real error_messages.yaml/hints.yaml
    // template in this codebase (issue #14) is plain variable interpolation only -- no
    // conditionals, filters, or loops -- so this single-pass token substitution is a byte-for-byte
    // behavioral match for Python's `_jinja_env.from_string(template_str).render(**kwargs)` for
    // every template that actually ships, without pulling in a full template-engine dependency.
    private static readonly Regex TemplateToken = new(@"\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    /// <summary>Renders error_messages.yaml's <paramref name="key"/> template with
    /// <paramref name="variables"/> substituted in for every <c>{{name}}</c> token. Mirrors
    /// prompt_loader.py's <c>render_error</c>: an unknown key logs and returns
    /// <c>"An error occurred (&lt;key&gt;)."</c> (never crashes a live tool call); a template
    /// referencing a variable that wasn't supplied (Jinja2's <c>StrictUndefined</c> raising, in
    /// Python) returns the RAW, unrendered template text instead of throwing or silently leaving a
    /// half-substituted string.</summary>
    public string RenderError(string key, IReadOnlyDictionary<string, object?>? variables = null)
    {
        if (!ErrorMessages.TryGetValue(key, out var raw) || raw is not string template)
        {
            return $"An error occurred ({key}).";
        }
        return RenderTemplate(template, variables);
    }

    /// <summary>Renders any Jinja2-style template string with the given variables -- mirrors
    /// prompt_loader.py's <c>render_template</c>. Returns the raw template unchanged if any
    /// <c>{{token}}</c> it contains has no matching entry in <paramref name="variables"/> (Python's
    /// <c>StrictUndefined</c>-triggered exception fallback), rather than partially substituting or
    /// throwing.</summary>
    public string RenderTemplate(string template, IReadOnlyDictionary<string, object?>? variables = null)
    {
        variables ??= new Dictionary<string, object?>();
        var missing = TemplateToken.Matches(template)
            .Select(m => m.Groups[1].Value)
            .Any(name => !variables.ContainsKey(name));
        if (missing)
        {
            return template;
        }
        return TemplateToken.Replace(template, m =>
        {
            var value = variables[m.Groups[1].Value];
            return value switch
            {
                null => "",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };
        });
    }

    /// <summary>The delta-text template for 'add'/'modify'/any other (treated as 'remove') action
    /// (hints.yaml's <c>delta_templates</c>), falling back to the same hardcoded default text
    /// tools.py itself falls back to when no PromptLoader is bound at all. Mirrors
    /// prompt_loader.py's <c>get_delta_template</c>.</summary>
    public string GetDeltaTemplate(string action)
    {
        var templates = GetHintsSection("delta_templates");
        return action switch
        {
            "add" => GetString(templates, "item_added",
                "Added {{quantity}} {{display_name}} — your total is now {{total}}"),
            "modify" => GetString(templates, "item_modified",
                "Changed {{display_name}} — your total is now {{total}}"),
            _ => GetString(templates, "item_removed",
                "Removed {{quantity}} {{display_name}} — your total is now {{total}}"),
        };
    }

    /// <summary>The upsell-hint suffix (leading space + parens, or "" for no hint) for
    /// <paramref name="category"/> -- the first <c>hints.yaml</c> <c>upsell_hints</c> entry whose
    /// own <c>trigger_categories</c> contains it, falling back to the "generic" entry's own hint.
    /// Mirrors prompt_loader.py's <c>get_upsell_hint</c> exactly, including iterating every entry
    /// (not skipping "generic") in the match loop -- harmless in practice since no real pack's own
    /// "generic" entry sets <c>trigger_categories</c>, but keeping the C# port a literal mirror of
    /// the Python control flow rather than relying on that data-shape assumption.</summary>
    public string GetUpsellHint(string category)
    {
        if (Hints.TryGetValue("upsell_hints", out var upsellRaw) && upsellRaw is IDictionary<object, object> upsellHints)
        {
            foreach (var entry in upsellHints)
            {
                if (entry.Value is IDictionary<object, object> info &&
                    info.TryGetValue("trigger_categories", out var triggersRaw) &&
                    triggersRaw is IEnumerable<object> triggers &&
                    triggers.Any(t => string.Equals(t?.ToString(), category, StringComparison.Ordinal)))
                {
                    return info.TryGetValue("hint", out var hint) && hint is string hintText
                        ? $" ({hintText})"
                        : "";
                }
            }
            if (upsellHints.TryGetValue("generic", out var genericRaw) &&
                genericRaw is IDictionary<object, object> genericInfo &&
                genericInfo.TryGetValue("hint", out var genericHint) && genericHint is string genericText &&
                genericText.Length > 0)
            {
                return $" ({genericText})";
            }
        }
        return "";
    }

    private IDictionary<object, object> GetHintsSection(string key) =>
        Hints.TryGetValue(key, out var value) && value is IDictionary<object, object> mapping
            ? mapping
            : new Dictionary<object, object>();

    private static string GetString(IDictionary<object, object> mapping, string key, string fallback) =>
        mapping.TryGetValue(key, out var value) && value is string s ? s : fallback;

    private IReadOnlyDictionary<object, object> LoadYaml(string promptsDir, string fileName)
    {
        var path = Path.Combine(promptsDir, fileName);
        if (!File.Exists(path))
        {
            throw new PromptLoadException($"Prompt file not found: {fileName} (persona '{PersonaId}').");
        }

        object? raw;
        try
        {
            raw = _yaml.Deserialize<object?>(File.ReadAllText(path));
        }
        catch (YamlException exc)
        {
            throw new PromptLoadException($"Malformed YAML in {fileName} (persona '{PersonaId}'): {exc.Message}");
        }

        if (raw is not IDictionary<object, object> mapping)
        {
            throw new PromptLoadException(
                $"{fileName} must be a YAML mapping, got {raw?.GetType().Name ?? "null"} (persona '{PersonaId}').");
        }

        return (IReadOnlyDictionary<object, object>)mapping;
    }

    private string AssembleSystemPrompt(IReadOnlyDictionary<object, object> data)
    {
        if (!data.TryGetValue("sections", out var sectionsRaw) || sectionsRaw is not IEnumerable<object> sections)
        {
            throw new PromptLoadException(
                $"system_prompt.yaml must have a 'sections' list (persona '{PersonaId}').");
        }

        var ordered = sections
            .OfType<IDictionary<object, object>>()
            .OrderBy(section => section.TryGetValue("priority", out var p) ? ParsePriority(p) : 999);

        var parts = ordered
            .Select(section => section.TryGetValue("content", out var c) ? c?.ToString()?.Trim() : null)
            .Where(content => !string.IsNullOrEmpty(content))
            .ToList();

        if (parts.Count == 0)
        {
            throw new PromptLoadException(
                $"system_prompt.yaml sections produced an empty prompt (persona '{PersonaId}').");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>YamlDotNet's default untyped Deserialize&lt;object?&gt;() returns every scalar as
    /// a plain string (it does not infer int/bool from YAML's core schema unless explicitly
    /// configured) -- so "priority: 1" comes back as the string "1", not the int 1. Handle both
    /// shapes so this sort works regardless of that deserializer detail.</summary>
    private static int ParsePriority(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => 999,
    };

    private IReadOnlyDictionary<object, object> ValidateGreeting(IReadOnlyDictionary<object, object> data)
    {
        var greeting = GetMapping(data, "greeting");
        if (greeting is null)
        {
            throw new PromptLoadException($"greeting.yaml must have a 'greeting' key (persona '{PersonaId}').");
        }
        if (!greeting.ContainsKey("type"))
        {
            throw new PromptLoadException($"greeting must have a 'type' field (persona '{PersonaId}').");
        }
        return (IReadOnlyDictionary<object, object>)greeting;
    }

    private List<IReadOnlyDictionary<object, object>> ValidateToolSchemas(IReadOnlyDictionary<object, object> data)
    {
        if (!data.TryGetValue("tools", out var toolsRaw) || toolsRaw is not IEnumerable<object> toolsEnumerable)
        {
            throw new PromptLoadException(
                $"tool_schemas.yaml must have a non-empty 'tools' list (persona '{PersonaId}').");
        }

        var tools = toolsEnumerable.OfType<IDictionary<object, object>>().ToList();
        if (tools.Count == 0)
        {
            throw new PromptLoadException(
                $"tool_schemas.yaml must have a non-empty 'tools' list (persona '{PersonaId}').");
        }

        for (var i = 0; i < tools.Count; i++)
        {
            if (!tools[i].ContainsKey("name"))
            {
                throw new PromptLoadException($"Tool at index {i} missing 'name' (persona '{PersonaId}').");
            }
            if (!tools[i].ContainsKey("type"))
            {
                throw new PromptLoadException($"Tool '{tools[i]["name"]}' missing 'type' (persona '{PersonaId}').");
            }
        }

        return tools.Select(t => (IReadOnlyDictionary<object, object>)t).ToList();
    }

    /// <summary>#125 (fail-fast follow-up to #116): every key in RequiredErrorMessageKeys must be
    /// present so the model never gets a bare "An error occurred" placeholder in place of the
    /// pack's own guidance for a real, reachable rejection path.</summary>
    private void ValidateErrorMessages(IReadOnlyDictionary<object, object> messages)
    {
        var missing = RequiredErrorMessageKeys.Where(key => !messages.ContainsKey(key)).ToList();
        if (missing.Count > 0)
        {
            throw new PromptLoadException(
                $"error_messages.yaml for persona pack '{PersonaId}' is missing required " +
                $"rejection-message key(s): {string.Join(", ", missing)}.");
        }
    }

    private static IDictionary<object, object>? GetMapping(IReadOnlyDictionary<object, object> data, string key) =>
        data.TryGetValue(key, out var value) ? value as IDictionary<object, object> : null;
}

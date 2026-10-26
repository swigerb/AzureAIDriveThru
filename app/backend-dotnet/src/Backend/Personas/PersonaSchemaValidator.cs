using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Backend.Personas;

/// <summary>
/// Validates a persona.json/menuItems.json JsonNode against the SAME schema file
/// (personas/persona.schema.json or personas/menu.schema.json) both backends and CI validate
/// against (docs/dotnet_mapping.md: jsonschema -> JsonSchema.Net). This is layer 1 of the
/// two-layer validation persona_loader.py also does; layer 2 (strongly-typed deserialization with
/// JsonUnmappedMemberHandling.Disallow) happens in PersonaCatalog.
/// </summary>
internal static class PersonaSchemaValidator
{
    private static readonly EvaluationOptions s_options = new() { OutputFormat = OutputFormat.List };

    /// <exception cref="PersonaValidationException">The instance violates the schema. The message
    /// names every violated instance location (JSON pointer) and the schema keyword that rejected
    /// it, e.g. "$.voice: required".</exception>
    public static void Validate(JsonSchema schema, JsonNode? instance, string personaId, string fileLabel)
    {
        EvaluationResults results = schema.Evaluate(JsonSerializer.SerializeToElement(instance), s_options);
        if (results.IsValid)
        {
            return;
        }

        var failures = new List<string>();
        CollectFailures(results, failures);

        string detail = failures.Count > 0
            ? string.Join("; ", failures)
            : "schema validation failed";

        throw new PersonaValidationException(
            $"Persona '{personaId}': {fileLabel} failed schema validation: {detail}");
    }

    private static void CollectFailures(EvaluationResults results, List<string> failures)
    {
        if (results.Errors is { Count: > 0 } errors)
        {
            string location = results.InstanceLocation.ToString();
            string location_ = string.IsNullOrEmpty(location) ? "$" : $"${location}";
            foreach ((string? keyword, string? message) in errors)
            {
                failures.Add($"{location_} ({keyword}): {message}");
            }
        }

        if (results.Details is not { Count: > 0 } details)
        {
            return;
        }

        foreach (EvaluationResults detail in details)
        {
            if (!detail.IsValid)
            {
                CollectFailures(detail, failures);
            }
        }
    }
}

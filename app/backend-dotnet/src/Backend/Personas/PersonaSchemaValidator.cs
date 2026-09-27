using System.Text;
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
public static class PersonaSchemaValidator
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    /// <exception cref="PersonaValidationException">The instance violates the schema. The message
    /// names every violated instance location (JSON pointer) and the schema keyword that rejected
    /// it, e.g. "$.voice: required".</exception>
    public static void Validate(JsonSchema schema, JsonNode? instance, string personaId, string fileLabel)
    {
        var results = schema.Evaluate(instance, Options);
        if (results.IsValid)
        {
            return;
        }

        var failures = new List<string>();
        CollectFailures(results, failures);

        var detail = failures.Count > 0
            ? string.Join("; ", failures)
            : "schema validation failed";

        throw new PersonaValidationException(
            $"Persona '{personaId}': {fileLabel} failed schema validation: {detail}");
    }

    private static void CollectFailures(EvaluationResults results, List<string> failures)
    {
        if (results.HasErrors)
        {
            var location = results.InstanceLocation.ToString();
            var location_ = string.IsNullOrEmpty(location) ? "$" : $"${location}";
            foreach (var (keyword, message) in results.Errors!)
            {
                failures.Add($"{location_} ({keyword}): {message}");
            }
        }

        if (!results.HasDetails)
        {
            return;
        }

        foreach (var detail in results.Details)
        {
            if (!detail.IsValid)
            {
                CollectFailures(detail, failures);
            }
        }
    }
}

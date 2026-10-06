namespace Backend.Personas;

/// <summary>
/// Raised for any problem with a persona pack -- missing/empty personas directory, malformed
/// JSON, a JSON Schema violation, an id/folder mismatch, a missing menu or prompts directory, or
/// an env-var (PERSONAS/DEFAULT_PERSONA) misconfiguration. Mirrors app/backend/persona_loader.py's
/// PersonaValidationError: the host (Program.cs) lets this propagate out of startup unhandled so
/// the process exits non-zero and never serves traffic with a partially/invalidly loaded catalog
/// (ADR-001 decision 2, "fail fast on an invalid pack").
/// </summary>
internal sealed class PersonaValidationException(string message) : Exception(message);

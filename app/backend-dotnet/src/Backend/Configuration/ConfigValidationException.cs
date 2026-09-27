namespace Backend.Configuration;

/// <summary>Mirrors app/backend/persona_loader.py's PersonaValidationError style: names the file
/// and what's wrong, thrown out of Program.cs startup uncaught so the process exits non-zero
/// instead of serving traffic with a missing/malformed config.</summary>
public sealed class ConfigValidationException(string message) : Exception(message);

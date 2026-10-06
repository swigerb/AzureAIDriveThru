namespace Backend.Health;

/// <summary>Port of app/backend/app.py's module-level `_startup_checks` dict, read by /health.
/// Registered as a singleton so Program.cs's startup sequence and the /health handler share the
/// same instance.</summary>
internal sealed class StartupChecks
{
    private readonly Dictionary<string, bool> _checks = new(StringComparer.Ordinal)
    {
        ["env_vars"] = false,
        ["personas_loaded"] = false,
        ["prompts_loaded"] = false,
        // Matches app/backend/app.py's _startup_checks: hardcoded true, not a real toggle.
        // Program.cs loads AppConfig strictly before /health is ever reachable (it returns 1 and
        // never calls app.Run() on a ConfigValidationException) -- reaching this class at all
        // already proves config loaded, exactly like Python's own "validated at module load by
        // get_config()" comment.
        ["config_loaded"] = true,
    };

    public IReadOnlyDictionary<string, bool> Checks => _checks;

    public bool AllPassed => _checks.Values.All(passed => passed);

    public void Pass(string name)
    {
        if (!_checks.ContainsKey(name))
        {
            throw new ArgumentOutOfRangeException(nameof(name), $"Unknown startup check '{name}'.");
        }
        _checks[name] = true;
    }
}

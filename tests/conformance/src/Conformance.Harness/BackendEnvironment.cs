using System.Security.Cryptography;

namespace Conformance.Harness;

/// <summary>
/// Python-launcher-specific extras layered on top of the neutral <see cref="BackendContract"/> —
/// knobs that only make sense for *this* launcher (env var overrides for CONFORMANCE_TEST_HOOKS).
/// A future .NET launcher (S2) would have its own equivalent options type instead of reusing this
/// one, while both share the same <see cref="BackendContract"/>.
/// </summary>
public sealed class PythonBackendOptions
{
    /// <summary>
    /// Extra environment variables layered on top of the defaults — used to enable
    /// CONFORMANCE_TEST_HOOKS=1 plus its overrides (fixed clock, short timers) for scenarios
    /// that need them. Empty by default so most scenarios run against real production timing.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Builds the exact environment variable set `app/backend/app.py` needs to start against the
/// fakes, tracing every env var it reads (see app.py / order_state.py / tools.py / rtmt.py).
/// Defaults RUNNING_IN_PRODUCTION=true so the backend never calls `load_dotenv()` and picks
/// up a developer's local `.env` — every value the process needs is set explicitly here instead,
/// which keeps the harness deterministic regardless of what's on a given machine. Exception:
/// Rick's PR #118 review item 4 added app.py's own startup guard that refuses to start with
/// CONFORMANCE_TEST_HOOKS=1 alongside RUNNING_IN_PRODUCTION=true (a real deployment must never
/// run with test hooks live) -- and every <see cref="BackendProfile"/> except
/// <see cref="BackendProfiles.HooksOff"/> sets CONFORMANCE_TEST_HOOKS=1 to unlock the S1
/// response.create allow-list behaviour (see BackendProfiles.Default's own doc comment), so this
/// builder downgrades RUNNING_IN_PRODUCTION to "false" whenever the caller's ExtraEnvironment
/// enables test hooks -- a conformance-harness process with hooks on is never modelling a real
/// deployment, so the guard must not fire for it. Only <see cref="BackendProfiles.HooksOff"/>
/// (CONFORMANCE_TEST_HOOKS left unset, "the exact shape of a real deployment") keeps
/// RUNNING_IN_PRODUCTION=true, matching what main.bicep actually sets in Azure.
/// </summary>
public static class BackendEnvironment
{
    public static Dictionary<string, string> Build(BackendContract contract, PythonBackendOptions options)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── Neutral BackendContract: any backend implementation needs these. PR #22 review
            // item N8 moved RUNNING_IN_PRODUCTION / LOG_LEVEL / APP_SESSION_SECRET /
            // RATE_LIMIT_RECOVERY_ENABLED into this group — none of their *names* are
            // Python-specific (unlike PYTHONUNBUFFERED/PYTHONUTF8 below), and a future .NET
            // backend under test would equally need "run as if production" behaviour, an
            // explicit log level, its own session-token secret, and rate-limit recovery enabled
            // to match the scenarios this suite drives; only the concrete launcher wiring for a
            // different language would differ, not whether these are needed at all. ──
            ["HOST"] = BackendContract.Host,
            ["PORT"] = contract.Port.ToString(),

            // Key auth (never DefaultAzureCredential/AzureDeveloperCliCredential in CI).
            ["AZURE_OPENAI_EASTUS2_API_KEY"] = BackendContract.OpenAiApiKey,
            ["AZURE_SEARCH_API_KEY"] = BackendContract.SearchApiKey,

            // Point straight at the fakes.
            ["AZURE_OPENAI_EASTUS2_ENDPOINT"] = contract.RealtimeBaseUri.ToString().TrimEnd('/'),
            ["AZURE_OPENAI_REALTIME_DEPLOYMENT"] = contract.Deployment,
            ["AZURE_OPENAI_REALTIME_VOICE_CHOICE"] = contract.Voice,
            ["AZURE_SEARCH_ENDPOINT"] = contract.SearchBaseUri.ToString().TrimEnd('/'),
            ["AZURE_SEARCH_INDEX"] = contract.SearchIndex,
            ["AZURE_SEARCH_SEMANTIC_CONFIGURATION"] = BackendContract.SearchSemanticConfiguration,
            ["AZURE_SEARCH_IDENTIFIER_FIELD"] = BackendContract.SearchIdentifierField,
            ["AZURE_SEARCH_CONTENT_FIELD"] = BackendContract.SearchContentField,
            ["AZURE_SEARCH_EMBEDDING_FIELD"] = BackendContract.SearchEmbeddingField,
            ["AZURE_SEARCH_TITLE_FIELD"] = BackendContract.SearchTitleField,
            ["AZURE_SEARCH_USE_VECTOR_QUERY"] = BackendContract.SearchUseVectorQuery ? "true" : "false",
            ["AZURE_SEARCH_SEMANTIC_RANKER"] = BackendContract.SearchSemanticRanker,
            ["STORE_TIMEZONE"] = contract.StoreTimeZone,
            ["RUNNING_IN_PRODUCTION"] = "true",
            ["LOG_LEVEL"] = "INFO",
            // Single-process HMAC secret; random per launch is fine since only this process
            // ever needs to validate tokens it issued itself.
            ["APP_SESSION_SECRET"] = RandomSecret(),
            ["RATE_LIMIT_RECOVERY_ENABLED"] = "true",

            // Issue #76: mirrors app/backend/persona_loader.py's own PERSONAS/DEFAULT_PERSONA env
            // vars explicitly, instead of relying on the backend's identical fallback defaults --
            // see ConformancePersonas' own doc comment for why this is a no-op behaviour change
            // today (only personas/sonic exists on disk).
            ["PERSONAS"] = string.Join(",", contract.Personas),
            ["DEFAULT_PERSONA"] = contract.DefaultPersona,

            // ── Python-launcher-specific extras: quirks of this particular process (the
            // CPython interpreter's own env vars), not part of the neutral contract a future
            // .NET launcher would also need to satisfy. ──
            ["PYTHONUNBUFFERED"] = "1",
            ["PYTHONUTF8"] = "1",
        };

        // Rick's PR #102 review item 1 (the two-pack persona_mismatch conformance row): only set
        // when a fixture explicitly overrides it (BackendContract.ForPort's personasDir param) --
        // omitted entirely otherwise, so every existing scenario resolves personas from the real
        // repo personas/ folder exactly as before.
        if (contract.PersonasDir is not null)
        {
            env["PERSONAS_DIR"] = contract.PersonasDir;
        }

        foreach (var (key, value) in options.ExtraEnvironment)
        {
            env[key] = value;
        }

        // Rick's PR #118 review item 4: app.py's own startup guard refuses to run with
        // CONFORMANCE_TEST_HOOKS=1 alongside RUNNING_IN_PRODUCTION=true. Every profile except
        // BackendProfiles.HooksOff sets CONFORMANCE_TEST_HOOKS=1 (see that profile's own doc
        // comment), so once hooks are on this is never modelling a real deployment -- downgrade
        // RUNNING_IN_PRODUCTION so the guard doesn't fire and this harness can keep launching the
        // fakes-backed backend every other scenario in this suite depends on. Uses conformance_
        // hooks.py's own exact-match semantics (only the literal string "1" enables hooks --
        // README.md's "Test hooks" table) so this stays in lockstep with the guard it's working
        // around.
        if (env.TryGetValue("CONFORMANCE_TEST_HOOKS", out var hooks) && hooks == "1")
        {
            env["RUNNING_IN_PRODUCTION"] = "false";
        }

        return env;
    }

    private static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

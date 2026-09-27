namespace Conformance.Harness;

/// <summary>
/// The neutral, language-agnostic contract every backend-under-test must satisfy to run against
/// the conformance fakes — the facts ANY backend implementation (Python today, the future .NET
/// backend once S2 exists per issue #7) needs to know, independent of how a specific launcher
/// wires them in (env vars for Python; appsettings/env for .NET; etc). Per-launcher extras (e.g.
/// <see cref="PythonBackendOptions"/>) layer their own language-specific knobs on top of this.
/// See tests/conformance/README.md's "BackendContract" table for the full HOST/PORT/`/health`
/// shape and shared-file list too.
/// </summary>
public sealed record BackendContract(
    Uri RealtimeBaseUri,
    Uri SearchBaseUri,
    int Port,
    string Deployment,
    string Voice,
    string SearchIndex,
    string StoreTimeZone,
    IReadOnlyList<string> Personas,
    string DefaultPersona,
    string? PersonasDir = null)
{
    public const string DefaultDeployment = "gpt-realtime-2.1-conformance";
    public const string DefaultVoice = "marin";
    public const string DefaultSearchIndex = "menu-index";
    public const string DefaultStoreTimeZone = "America/Chicago";

    /// <summary>Every backend under test binds to loopback only — the fakes and the suite never need to be reachable off-box.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>
    /// The fixed `AZURE_OPENAI_EASTUS2_API_KEY`-equivalent value every backend must send as the
    /// upstream realtime `api-key` header under key auth. Exposed here (not per-launcher) so
    /// <c>ConformanceFixture</c> can configure <see cref="Conformance.Fakes.FakeRealtimeUpstreamServer.ExpectedApiKey"/>
    /// to the exact same value instead of duplicating the literal, regardless of which launcher is active.
    /// </summary>
    public const string OpenAiApiKey = "conformance-test-openai-key";

    /// <summary>The fixed Azure AI Search API key value, analogous to <see cref="OpenAiApiKey"/>.</summary>
    public const string SearchApiKey = "conformance-test-search-key";

    public const string SearchSemanticConfiguration = "menuSemanticConfig";
    public const string SearchIdentifierField = "id";
    public const string SearchContentField = "description";
    public const string SearchEmbeddingField = "embedding";
    public const string SearchTitleField = "name";
    public const bool SearchUseVectorQuery = true;
    public const string SearchSemanticRanker = "standard";

    /// <summary>
    /// Issue #76: the persona(s) the launched backend is told about via PERSONAS, and which one
    /// is DEFAULT_PERSONA -- mirrors app/backend/persona_loader.py's own defaults (see
    /// <see cref="Conformance.Harness.ConformancePersonas"/>) so a caller that doesn't care about
    /// personas gets exactly today's implicit behaviour (everything runs as "sonic") with no
    /// change required.
    ///
    /// <paramref name="personasDir"/> (Rick's PR #102 review item 1: the two-pack persona_mismatch
    /// conformance row) overrides PERSONAS_DIR so a scenario can launch the backend against a
    /// second, TEST-ONLY persona pack directory (e.g. the same fixture pack
    /// app/backend/tests/test_persona_binding.py already uses) without needing a real, user-facing
    /// second persona pack under personas/ -- that's #78/#79's job, explicitly out of scope here.
    /// Null (the default) never sets PERSONAS_DIR, so every existing caller resolves personas from
    /// the real repo personas/ folder exactly as before.
    /// </summary>
    public static BackendContract ForPort(
        Uri realtimeBaseUri, Uri searchBaseUri, int port, string? deployment = null,
        IReadOnlyList<string>? personas = null, string? defaultPersona = null, string? personasDir = null) => new(
        RealtimeBaseUri: realtimeBaseUri,
        SearchBaseUri: searchBaseUri,
        Port: port,
        Deployment: deployment ?? DefaultDeployment,
        Voice: DefaultVoice,
        SearchIndex: DefaultSearchIndex,
        StoreTimeZone: DefaultStoreTimeZone,
        Personas: personas ?? [ConformancePersonas.DefaultPersonaId],
        DefaultPersona: defaultPersona ?? ConformancePersonas.DefaultPersonaId,
        PersonasDir: personasDir);
}

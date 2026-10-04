namespace SearchIndexRequestBuilder;

/// <summary>
/// Resolves the Azure OpenAI endpoint/embedding-deployment values the real
/// setup_search_index.py's <c>run()</c> reads from environment variables (lines 471-473) -- plus
/// an optional CLI-flag override this C# port adds (the Python script has none; every value there
/// always comes from the azd-managed environment or process env, never an argument). These values
/// only flow into the printed index definition's "vectorizers" section (resourceUri/deploymentId)
/// -- <see cref="SearchIndexRequestPlanner"/> never uses them to open a real connection -- but a
/// real human/CI run's preview output should reflect the real environment's configured
/// endpoint/deployment, not a fixed placeholder (PR #250 review R4).
///
/// Precedence matches setup_search_index.py's own <c>main()</c> (lines 535-539): it calls
/// <c>load_azd_env()</c> -- <c>load_dotenv(path, override=True)</c> -- BEFORE <c>run()</c> reads
/// <c>os.environ</c>, so an azd-managed value always wins over a pre-existing process environment
/// variable of the same name. This port's precedence is therefore CLI flag (Python has none) &gt;
/// azd default-environment value (<paramref name="azdEnvValues"/>, see
/// <see cref="AzdEnvLoader"/>) &gt; process environment variable &gt; (deployment only) the
/// built-in default. (PR #250 review R5.)
/// </summary>
internal static class OpenAiSettingsResolver
{
    /// <summary>setup_search_index.py's own <c>EMBEDDING_MODEL</c> constant (line 85) -- the
    /// default <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> falls back to when unset, matching
    /// <c>os.environ.get("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", EMBEDDING_MODEL)</c>.</summary>
    public const string DefaultEmbeddingDeployment = "text-embedding-3-large";

    private const string OpenAiEndpointVariableName = "AZURE_OPENAI_EASTUS2_ENDPOINT";
    private const string EmbeddingDeploymentVariableName = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT";

    private static readonly IReadOnlyDictionary<string, string> EmptyAzdValues =
        new Dictionary<string, string>();

    public sealed record Settings(string OpenAiEndpoint, string EmbeddingDeployment);

    /// <param name="openAiEndpointFlag">This port's own <c>--openai-endpoint</c> CLI flag, if
    /// given. Takes priority over both the azd env value and the environment variable.</param>
    /// <param name="embeddingDeploymentFlag">This port's own <c>--embedding-deployment</c> CLI
    /// flag, if given. Takes priority over both the azd env value and the environment
    /// variable.</param>
    /// <param name="getEnvironmentVariable">Normally omitted -- defaults to
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>. Lets tests resolve against a fake
    /// environment instead of mutating real process-wide environment variables (which would be
    /// both fragile under xunit's parallel test execution and leave residue for other tests).</param>
    /// <param name="azdEnvValues">Normally omitted (treated as empty) -- the azd default
    /// environment's own KEY/VALUE pairs, as loaded by <see cref="AzdEnvLoader"/>. A value present
    /// here beats <paramref name="getEnvironmentVariable"/>, matching
    /// <c>load_dotenv(path, override=True)</c>'s own precedence over a pre-existing process
    /// environment variable of the same name -- but NOT an empty-string value: see the
    /// "known divergence" note below.</param>
    /// <exception cref="InvalidOperationException">No <c>--openai-endpoint</c> flag, no azd env
    /// value, and no <c>AZURE_OPENAI_EASTUS2_ENDPOINT</c> environment variable.
    /// setup_search_index.py's own equivalent (<c>os.environ["AZURE_OPENAI_EASTUS2_ENDPOINT"]</c>)
    /// raises an unhandled <c>KeyError</c> with a Python stack trace in this situation; this port
    /// raises a clean, single-line, catchable message instead -- <see cref="CliRunner"/> turns it
    /// into a non-zero exit, not a stack trace.</exception>
    /// <remarks>
    /// Known, accepted divergence (documented in docs/dotnet_tooling.md): if the azd default
    /// environment OR the process environment sets <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> to the
    /// empty string, Python's <c>os.environ.get(name, default)</c> still returns that empty string
    /// (the key exists, even though its value is empty) and would send <c>""</c> as the
    /// deployment id; this port treats an empty string the same as "not set" at every source and
    /// falls back to the next source / the built-in default instead. This can never happen for the
    /// endpoint (an empty endpoint is treated as "not configured" and raises, matching Python
    /// attempting to call a real Azure OpenAI client with an empty base URL, which would also
    /// fail -- just with a different, less clean, error).
    /// </remarks>
    public static Settings Resolve(
        string? openAiEndpointFlag,
        string? embeddingDeploymentFlag,
        Func<string, string?>? getEnvironmentVariable = null,
        IReadOnlyDictionary<string, string>? azdEnvValues = null)
    {
        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var azdValues = azdEnvValues ?? EmptyAzdValues;

        var openAiEndpoint = openAiEndpointFlag;
        if (string.IsNullOrEmpty(openAiEndpoint))
        {
            openAiEndpoint = GetNonEmptyOrNull(azdValues, OpenAiEndpointVariableName);
        }
        if (string.IsNullOrEmpty(openAiEndpoint))
        {
            openAiEndpoint = getEnv(OpenAiEndpointVariableName);
        }
        if (string.IsNullOrEmpty(openAiEndpoint))
        {
            throw new InvalidOperationException(
                "Azure OpenAI endpoint not set -- pass --openai-endpoint <url>, set it in the azd " +
                "default environment (azd env set AZURE_OPENAI_EASTUS2_ENDPOINT <url>), or set the " +
                "AZURE_OPENAI_EASTUS2_ENDPOINT environment variable.");
        }

        var embeddingDeployment = embeddingDeploymentFlag;
        if (string.IsNullOrEmpty(embeddingDeployment))
        {
            embeddingDeployment = GetNonEmptyOrNull(azdValues, EmbeddingDeploymentVariableName);
        }
        if (string.IsNullOrEmpty(embeddingDeployment))
        {
            embeddingDeployment = getEnv(EmbeddingDeploymentVariableName);
        }
        if (string.IsNullOrEmpty(embeddingDeployment))
        {
            embeddingDeployment = DefaultEmbeddingDeployment;
        }

        return new Settings(openAiEndpoint, embeddingDeployment);
    }

    private static string? GetNonEmptyOrNull(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
}

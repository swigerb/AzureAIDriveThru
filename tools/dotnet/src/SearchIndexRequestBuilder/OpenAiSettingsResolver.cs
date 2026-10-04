namespace SearchIndexRequestBuilder;

/// <summary>
/// Resolves the Azure OpenAI endpoint/embedding-deployment values the real
/// setup_search_index.py's <c>run()</c> reads from environment variables (lines 471-473) -- plus
/// an optional CLI-flag override this C# port adds (the Python script has none; every value there
/// always comes from the azd-managed environment, never an argument). These values only flow into
/// the printed index definition's "vectorizers" section (resourceUri/deploymentId) --
/// <see cref="SearchIndexRequestPlanner"/> never uses them to open a real connection -- but a real
/// human/CI run's preview output should reflect the real environment's configured
/// endpoint/deployment, not a fixed placeholder (PR #250 review R4).
/// </summary>
internal static class OpenAiSettingsResolver
{
    /// <summary>setup_search_index.py's own <c>EMBEDDING_MODEL</c> constant (line 85) -- the
    /// default <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> falls back to when unset, matching
    /// <c>os.environ.get("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", EMBEDDING_MODEL)</c>.</summary>
    public const string DefaultEmbeddingDeployment = "text-embedding-3-large";

    public sealed record Settings(string OpenAiEndpoint, string EmbeddingDeployment);

    /// <param name="openAiEndpointFlag">This port's own <c>--openai-endpoint</c> CLI flag, if
    /// given. Takes priority over the environment variable.</param>
    /// <param name="embeddingDeploymentFlag">This port's own <c>--embedding-deployment</c> CLI
    /// flag, if given. Takes priority over the environment variable.</param>
    /// <param name="getEnvironmentVariable">Normally omitted -- defaults to
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>. Lets tests resolve against a fake
    /// environment instead of mutating real process-wide environment variables (which would be
    /// both fragile under xunit's parallel test execution and leave residue for other tests).</param>
    /// <exception cref="InvalidOperationException">No <c>--openai-endpoint</c> flag and no
    /// <c>AZURE_OPENAI_EASTUS2_ENDPOINT</c> environment variable. setup_search_index.py's own
    /// equivalent (<c>os.environ["AZURE_OPENAI_EASTUS2_ENDPOINT"]</c>) raises an unhandled
    /// <c>KeyError</c> with a Python stack trace in this situation; this port raises a clean,
    /// single-line, catchable message instead -- <see cref="CliRunner"/> turns it into a non-zero
    /// exit, not a stack trace.</exception>
    public static Settings Resolve(
        string? openAiEndpointFlag,
        string? embeddingDeploymentFlag,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        var openAiEndpoint = openAiEndpointFlag;
        if (string.IsNullOrEmpty(openAiEndpoint))
        {
            openAiEndpoint = getEnv("AZURE_OPENAI_EASTUS2_ENDPOINT");
        }
        if (string.IsNullOrEmpty(openAiEndpoint))
        {
            throw new InvalidOperationException(
                "Azure OpenAI endpoint not set -- pass --openai-endpoint <url> or set the " +
                "AZURE_OPENAI_EASTUS2_ENDPOINT environment variable.");
        }

        var embeddingDeployment = embeddingDeploymentFlag;
        if (string.IsNullOrEmpty(embeddingDeployment))
        {
            embeddingDeployment = getEnv("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");
        }
        if (string.IsNullOrEmpty(embeddingDeployment))
        {
            embeddingDeployment = DefaultEmbeddingDeployment;
        }

        return new Settings(openAiEndpoint, embeddingDeployment);
    }
}

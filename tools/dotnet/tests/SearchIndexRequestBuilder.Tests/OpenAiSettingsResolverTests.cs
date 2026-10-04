namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Unit tests for OpenAiSettingsResolver's flag &gt; env var &gt; default priority order (PR #250
/// review R1): previously the CLI always printed hardcoded fake values
/// (resourceUri https://fake.openai.azure.com, deploymentId fake-embedding-deployment) regardless
/// of the real environment -- these tests guard the real resolution logic that replaced that.
/// </summary>
public sealed class OpenAiSettingsResolverTests
{
    [Fact]
    public void Resolve_UsesDefaultEmbeddingDeployment_WhenNeitherFlagNorEnvVarIsSet()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: "https://real.openai.azure.com",
            embeddingDeploymentFlag: null,
            getEnvironmentVariable: _ => null);

        Assert.Equal(OpenAiSettingsResolver.DefaultEmbeddingDeployment, settings.EmbeddingDeployment);
        Assert.Equal("text-embedding-3-large", settings.EmbeddingDeployment);
    }

    [Fact]
    public void Resolve_UsesEmbeddingDeploymentEnvVar_WhenFlagIsAbsent()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: "https://real.openai.azure.com",
            embeddingDeploymentFlag: null,
            getEnvironmentVariable: name => name == "AZURE_OPENAI_EMBEDDING_DEPLOYMENT" ? "text-embedding-3-small" : null);

        Assert.Equal("text-embedding-3-small", settings.EmbeddingDeployment);
    }

    [Fact]
    public void Resolve_EmbeddingDeploymentFlag_TakesPriorityOverEnvVar()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: "https://real.openai.azure.com",
            embeddingDeploymentFlag: "flag-deployment",
            getEnvironmentVariable: name => name == "AZURE_OPENAI_EMBEDDING_DEPLOYMENT" ? "env-deployment" : null);

        Assert.Equal("flag-deployment", settings.EmbeddingDeployment);
    }

    [Fact]
    public void Resolve_UsesOpenAiEndpointEnvVar_WhenFlagIsAbsent()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: null,
            embeddingDeploymentFlag: null,
            getEnvironmentVariable: name => name == "AZURE_OPENAI_EASTUS2_ENDPOINT" ? "https://env.openai.azure.com" : null);

        Assert.Equal("https://env.openai.azure.com", settings.OpenAiEndpoint);
    }

    [Fact]
    public void Resolve_OpenAiEndpointFlag_TakesPriorityOverEnvVar()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: "https://flag.openai.azure.com",
            embeddingDeploymentFlag: null,
            getEnvironmentVariable: name => name == "AZURE_OPENAI_EASTUS2_ENDPOINT" ? "https://env.openai.azure.com" : null);

        Assert.Equal("https://flag.openai.azure.com", settings.OpenAiEndpoint);
    }

    /// <summary>
    /// Matches setup_search_index.py's own required <c>os.environ["AZURE_OPENAI_EASTUS2_ENDPOINT"]</c>
    /// (no default) -- this port raises a clean, catchable, single-line message instead of letting
    /// a Python-style unhandled KeyError stack trace through; CliRunner turns this into a non-zero
    /// exit (see CliRunnerTests, if present, or CliRunner.cs's own try/catch).
    ///
    /// Mutation check performed (reverted after confirming): temporarily removing the
    /// "if (string.IsNullOrEmpty(openAiEndpoint)) throw ..." guard in OpenAiSettingsResolver.cs made
    /// this test fail (no exception thrown, and the returned endpoint was null) -- confirmed, then
    /// restored.
    /// </summary>
    [Fact]
    public void Resolve_Throws_WhenOpenAiEndpointIsMissing_FromBothFlagAndEnvVar()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: null,
            embeddingDeploymentFlag: null,
            getEnvironmentVariable: _ => null));

        Assert.Contains("--openai-endpoint", ex.Message);
        Assert.Contains("AZURE_OPENAI_EASTUS2_ENDPOINT", ex.Message);
    }

    [Fact]
    public void Resolve_TreatsEmptyStringFlag_SameAsMissingFlag()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: "",
            embeddingDeploymentFlag: "",
            getEnvironmentVariable: name => name switch
            {
                "AZURE_OPENAI_EASTUS2_ENDPOINT" => "https://env.openai.azure.com",
                "AZURE_OPENAI_EMBEDDING_DEPLOYMENT" => "env-deployment",
                _ => null,
            });

        Assert.Equal("https://env.openai.azure.com", settings.OpenAiEndpoint);
        Assert.Equal("env-deployment", settings.EmbeddingDeployment);
    }
}

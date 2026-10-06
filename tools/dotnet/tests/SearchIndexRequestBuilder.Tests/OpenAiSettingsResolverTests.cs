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

    // --- PR #250 review R5: azd default-environment precedence (setup_search_index.py's main()
    // calls load_azd_env() -- load_dotenv(path, override=True) -- before run() reads os.environ,
    // so an azd value always beats a pre-existing process environment variable). ---

    [Fact]
    public void Resolve_UsesAzdEnvValue_WhenNoFlagAndNoProcessEnvVar()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: null,
            embeddingDeploymentFlag: null,
            getEnvironmentVariable: _ => null,
            azdEnvValues: new Dictionary<string, string>
            {
                ["AZURE_OPENAI_EASTUS2_ENDPOINT"] = "https://azd.openai.azure.com",
                ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "azd-deployment",
            });

        Assert.Equal("https://azd.openai.azure.com", settings.OpenAiEndpoint);
        Assert.Equal("azd-deployment", settings.EmbeddingDeployment);
    }

    /// <summary>
    /// Matches python-dotenv's <c>load_dotenv(path, override=True)</c>: the azd-managed value
    /// REPLACES a pre-existing process environment variable of the same name, rather than only
    /// filling a gap -- the opposite of python-dotenv's own default (<c>override=False</c>).
    /// </summary>
    [Fact]
    public void Resolve_AzdEnvValue_TakesPriorityOverProcessEnvVar()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: null,
            embeddingDeploymentFlag: null,
            getEnvironmentVariable: name => name switch
            {
                "AZURE_OPENAI_EASTUS2_ENDPOINT" => "https://process-env.openai.azure.com",
                "AZURE_OPENAI_EMBEDDING_DEPLOYMENT" => "process-env-deployment",
                _ => null,
            },
            azdEnvValues: new Dictionary<string, string>
            {
                ["AZURE_OPENAI_EASTUS2_ENDPOINT"] = "https://azd.openai.azure.com",
                ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "azd-deployment",
            });

        Assert.Equal("https://azd.openai.azure.com", settings.OpenAiEndpoint);
        Assert.Equal("azd-deployment", settings.EmbeddingDeployment);
    }

    /// <summary>
    /// This port's own CLI-flag addition (Python has no equivalent) still wins over everything,
    /// including an azd-managed value.
    /// </summary>
    [Fact]
    public void Resolve_FlagTakesPriorityOverAzdEnvValue()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: "https://flag.openai.azure.com",
            embeddingDeploymentFlag: "flag-deployment",
            getEnvironmentVariable: _ => null,
            azdEnvValues: new Dictionary<string, string>
            {
                ["AZURE_OPENAI_EASTUS2_ENDPOINT"] = "https://azd.openai.azure.com",
                ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "azd-deployment",
            });

        Assert.Equal("https://flag.openai.azure.com", settings.OpenAiEndpoint);
        Assert.Equal("flag-deployment", settings.EmbeddingDeployment);
    }

    /// <summary>
    /// Documented, intentional divergence from Python (see OpenAiSettingsResolver.Resolve's own
    /// "known, accepted divergence" remarks): Python's <c>os.environ.get(name, default)</c> would
    /// still return an empty string set by <c>load_dotenv(override=True)</c> (the key exists, even
    /// though empty) and send <c>""</c> as the deployment id to Azure; this port treats an
    /// empty-string azd value the same as "not set" and falls through to the process env / the
    /// built-in default instead.
    /// </summary>
    [Fact]
    public void Resolve_TreatsEmptyStringAzdValue_SameAsMissing_FallsBackToProcessEnvThenDefault()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: "https://real.openai.azure.com",
            embeddingDeploymentFlag: null,
            getEnvironmentVariable: name => name == "AZURE_OPENAI_EMBEDDING_DEPLOYMENT" ? "process-env-deployment" : null,
            azdEnvValues: new Dictionary<string, string>
            {
                ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "",
            });

        Assert.Equal("process-env-deployment", settings.EmbeddingDeployment);
    }

    /// <summary>
    /// Same divergence as the deployment case above, but for the endpoint -- NOT "impossible for
    /// the endpoint" as an earlier draft of these docs incorrectly claimed. If the azd default
    /// environment has overwritten AZURE_OPENAI_EASTUS2_ENDPOINT with an empty string while a
    /// DIFFERENT, real, non-empty value is still sitting in the process environment (e.g. set
    /// directly by a developer's shell, bypassing azd), Python's own <c>load_dotenv(override=True)</c>
    /// would have already clobbered that real process-env value with the empty one by the time
    /// <c>run()</c> reads <c>os.environ["AZURE_OPENAI_EASTUS2_ENDPOINT"]</c> -- so Python ends up
    /// with the empty string, not the real value. This port instead falls through past the empty
    /// azd value to the real process-env value and SUCCEEDS with it, which is the same
    /// "this port recovers the real value where Python's override=True would have destroyed it"
    /// divergence as the deployment case, just manifesting as success-vs-silently-wrong instead of
    /// success-vs-silently-wrong-but-still-a-valid-string.
    /// </summary>
    [Fact]
    public void Resolve_TreatsEmptyStringAzdValue_ForEndpoint_SameAsMissing_FallsBackToProcessEnvThenSucceeds()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: null,
            embeddingDeploymentFlag: "flag-deployment",
            getEnvironmentVariable: name => name == "AZURE_OPENAI_EASTUS2_ENDPOINT" ? "https://real.openai.azure.com" : null,
            azdEnvValues: new Dictionary<string, string>
            {
                ["AZURE_OPENAI_EASTUS2_ENDPOINT"] = "",
            });

        Assert.Equal("https://real.openai.azure.com", settings.OpenAiEndpoint);
    }

    [Fact]
    public void Resolve_WorksWithNoAzdEnvValuesGiven_SameAsBeforeThisFeatureExisted()
    {
        var settings = OpenAiSettingsResolver.Resolve(
            openAiEndpointFlag: null,
            embeddingDeploymentFlag: null,
            getEnvironmentVariable: name => name == "AZURE_OPENAI_EASTUS2_ENDPOINT" ? "https://env.openai.azure.com" : null,
            azdEnvValues: null);

        Assert.Equal("https://env.openai.azure.com", settings.OpenAiEndpoint);
        Assert.Equal(OpenAiSettingsResolver.DefaultEmbeddingDeployment, settings.EmbeddingDeployment);
    }
}

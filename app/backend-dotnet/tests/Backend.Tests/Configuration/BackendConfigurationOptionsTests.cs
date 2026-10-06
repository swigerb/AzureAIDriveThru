using Backend.Configuration;
using Backend.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Backend.Tests.Configuration;

[Collection(EnvironmentVariableTestCollection.Name)]
public sealed class BackendConfigurationOptionsTests : IDisposable
{
    private static readonly string[] EnvVars =
    [
        BackendEnvironment.AzureOpenAiEastUs2Endpoint,
        BackendEnvironment.AzureOpenAiRealtimeDeployment,
        BackendEnvironment.AzureSearchApiKey,
        BackendEnvironment.AzureSearchContentField,
        BackendEnvironment.AzureSearchEmbeddingField,
        BackendEnvironment.AzureSearchEndpoint,
        BackendEnvironment.AzureSearchIdentifierField,
        BackendEnvironment.AzureSearchIndex,
        BackendEnvironment.AzureSearchSemanticConfiguration,
        BackendEnvironment.AzureSearchSemanticRanker,
        BackendEnvironment.AzureSearchUseVectorQuery,
    ];

    private readonly Dictionary<string, string?> _originalValues = EnvVars.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public void Dispose()
    {
        foreach (var (name, value) in _originalValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void RequiredBackendOptions_MissingVars_FailsWithProgramOrderedMessage()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchIndex, "menu-index");

        using var services = BuildServices();

        var ex = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<RequiredBackendOptions>>().Value);

        Assert.Equal(
            [
                "Missing required environment variables: " +
                "AZURE_OPENAI_EASTUS2_ENDPOINT, AZURE_OPENAI_REALTIME_DEPLOYMENT, AZURE_SEARCH_ENDPOINT"
            ],
            ex.Failures);
        Assert.Equal(
            "AZURE_OPENAI_EASTUS2_ENDPOINT, AZURE_OPENAI_REALTIME_DEPLOYMENT, AZURE_SEARCH_ENDPOINT",
            RequiredBackendOptionsValidator.GetMissingEnvironmentVariables(ex));
    }

    [Fact]
    public void RequiredBackendOptions_PresentVars_BindFromEnvironment()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureOpenAiEastUs2Endpoint, "https://openai.example");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureOpenAiRealtimeDeployment, "gpt-realtime");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchEndpoint, "https://search.example");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchIndex, "menu-index");

        using var services = BuildServices();

        var options = services.GetRequiredService<IOptions<RequiredBackendOptions>>().Value;

        Assert.Equal("https://openai.example", options.AzureOpenAiEastUs2Endpoint);
        Assert.Equal("gpt-realtime", options.AzureOpenAiRealtimeDeployment);
        Assert.Equal("https://search.example", options.AzureSearchEndpoint);
        Assert.Equal("menu-index", options.AzureSearchIndex);
    }

    [Fact]
    public void SearchEndpointConfig_MissingOptionalVars_UsesPythonFallbacks()
    {
        ClearAll();

        using var services = BuildServices();

        var config = services.GetRequiredService<IOptions<SearchEndpointConfig>>().Value;

        Assert.Equal(string.Empty, config.Endpoint);
        Assert.Null(config.ApiKey);
        Assert.Equal("menuSemanticConfig", config.SemanticConfiguration);
        Assert.Equal("id", config.IdentifierField);
        Assert.Equal("description", config.ContentField);
        Assert.Equal("embedding", config.EmbeddingField);
        Assert.True(config.UseVectorQuery);
        Assert.True(config.UseSemanticRanker);
    }

    [Fact]
    public void SearchEndpointConfig_BindsExplicitEnvironmentOverrides()
    {
        ClearAll();
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchEndpoint, "https://search.example");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchApiKey, "secret");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchSemanticConfiguration, "semantic");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchIdentifierField, "sku");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchContentField, "body");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchEmbeddingField, "vector");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchUseVectorQuery, "no");
        Environment.SetEnvironmentVariable(BackendEnvironment.AzureSearchSemanticRanker, "disabled");

        using var services = BuildServices();

        var config = services.GetRequiredService<SearchEndpointConfig>();

        Assert.Equal("https://search.example", config.Endpoint);
        Assert.Equal("secret", config.ApiKey);
        Assert.Equal("semantic", config.SemanticConfiguration);
        Assert.Equal("sku", config.IdentifierField);
        Assert.Equal("body", config.ContentField);
        Assert.Equal("vector", config.EmbeddingField);
        Assert.False(config.UseVectorQuery);
        Assert.False(config.UseSemanticRanker);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddBackendConfigurationOptions();
        return services.BuildServiceProvider();
    }

    private static void ClearAll()
    {
        foreach (var envVar in EnvVars)
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class EnvironmentVariableTestCollection
{
    public const string Name = "Backend configuration environment variable tests";
}

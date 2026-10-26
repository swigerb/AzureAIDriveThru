namespace SearchIndexIngestor.Tests;

/// <summary>
/// Unit tests for <see cref="SearchEndpointResolver"/>'s precedence (CLI flag &gt; azd env value
/// &gt; environment variable &gt; throw) and the <c>AZURE_SEARCH_SKIP_INDEX_SETUP</c> escape hatch
/// -- same shape as SearchIndexRequestBuilder.Tests/OpenAiSettingsResolverTests.cs for its sibling
/// resolver.
/// </summary>
public sealed class SearchEndpointResolverTests
{
    private static readonly IReadOnlyDictionary<string, string> s_emptyAzd = new Dictionary<string, string>();

    [Fact]
    public void Resolve_Throws_WhenNothingIsSet()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SearchEndpointResolver.Resolve(null, _ => null, s_emptyAzd));
        Assert.Contains("AZURE_SEARCH_ENDPOINT", ex.Message);
    }

    [Fact]
    public void Resolve_UsesEnvironmentVariable_WhenSet()
    {
        string endpoint = SearchEndpointResolver.Resolve(
            null, name => name == "AZURE_SEARCH_ENDPOINT" ? "https://env.search.windows.net" : null, s_emptyAzd);
        Assert.Equal("https://env.search.windows.net", endpoint);
    }

    [Fact]
    public void Resolve_AzdEnvValue_BeatsEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["AZURE_SEARCH_ENDPOINT"] = "https://azd.search.windows.net" };
        string endpoint = SearchEndpointResolver.Resolve(
            null, name => name == "AZURE_SEARCH_ENDPOINT" ? "https://env.search.windows.net" : null, azd);
        Assert.Equal("https://azd.search.windows.net", endpoint);
    }

    [Fact]
    public void Resolve_CliFlag_BeatsBothAzdAndEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["AZURE_SEARCH_ENDPOINT"] = "https://azd.search.windows.net" };
        string endpoint = SearchEndpointResolver.Resolve(
            "https://flag.search.windows.net",
            name => name == "AZURE_SEARCH_ENDPOINT" ? "https://env.search.windows.net" : null,
            azd);
        Assert.Equal("https://flag.search.windows.net", endpoint);
    }

    [Fact]
    public void IsSkipIndexSetupEnabled_False_WhenUnset()
    {
        Assert.False(SearchEndpointResolver.IsSkipIndexSetupEnabled(_ => null, s_emptyAzd));
    }

    [Fact]
    public void IsSkipIndexSetupEnabled_True_OnlyForExactLiteral_true()
    {
        Assert.True(SearchEndpointResolver.IsSkipIndexSetupEnabled(
            name => name == "AZURE_SEARCH_SKIP_INDEX_SETUP" ? "true" : null, s_emptyAzd));
        Assert.False(SearchEndpointResolver.IsSkipIndexSetupEnabled(
            name => name == "AZURE_SEARCH_SKIP_INDEX_SETUP" ? "TRUE" : null, s_emptyAzd));
        Assert.False(SearchEndpointResolver.IsSkipIndexSetupEnabled(
            name => name == "AZURE_SEARCH_SKIP_INDEX_SETUP" ? "1" : null, s_emptyAzd));
    }

    [Fact]
    public void IsSkipIndexSetupEnabled_AzdValue_BeatsEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["AZURE_SEARCH_SKIP_INDEX_SETUP"] = "true" };
        Assert.True(SearchEndpointResolver.IsSkipIndexSetupEnabled(
            name => name == "AZURE_SEARCH_SKIP_INDEX_SETUP" ? "false" : null, azd));
    }
}

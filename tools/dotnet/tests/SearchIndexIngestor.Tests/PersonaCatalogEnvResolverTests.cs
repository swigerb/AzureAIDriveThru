namespace SearchIndexIngestor.Tests;

/// <summary>
/// Unit tests for <see cref="PersonaCatalogEnvResolver"/>'s <c>PERSONAS</c>/<c>PERSONAS_DIR</c>
/// precedence (Rick's review, item 2a/round 2) -- same shape as
/// SearchEndpointResolverTests.cs/OpenAiSettingsResolverTests.cs for its sibling resolvers.
/// </summary>
public sealed class PersonaCatalogEnvResolverTests
{
    private static readonly IReadOnlyDictionary<string, string> s_emptyAzd = new Dictionary<string, string>();

    [Fact]
    public void ResolvePersonasDir_ReturnsNull_WhenNothingIsSet()
    {
        Assert.Null(PersonaCatalogEnvResolver.ResolvePersonasDir(null, _ => null, s_emptyAzd));
    }

    [Fact]
    public void ResolvePersonasDir_UsesEnvironmentVariable_WhenSet()
    {
        string? dir = PersonaCatalogEnvResolver.ResolvePersonasDir(
            null, name => name == "PERSONAS_DIR" ? "/env/personas" : null, s_emptyAzd);
        Assert.Equal("/env/personas", dir);
    }

    [Fact]
    public void ResolvePersonasDir_AzdEnvValue_BeatsEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["PERSONAS_DIR"] = "/azd/personas" };
        string? dir = PersonaCatalogEnvResolver.ResolvePersonasDir(
            null, name => name == "PERSONAS_DIR" ? "/env/personas" : null, azd);
        Assert.Equal("/azd/personas", dir);
    }

    [Fact]
    public void ResolvePersonasDir_Flag_BeatsBothAzdAndEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["PERSONAS_DIR"] = "/azd/personas" };
        string? dir = PersonaCatalogEnvResolver.ResolvePersonasDir(
            "/flag/personas", name => name == "PERSONAS_DIR" ? "/env/personas" : null, azd);
        Assert.Equal("/flag/personas", dir);
    }

    [Fact]
    public void ResolvePersonasDir_IgnoresAnEmptyAzdValue_FallingBackToTheEnvironmentVariable()
    {
        // Mirrors every other resolver's own GetNonEmptyOrNull treatment of an empty azd value --
        // an azd setting present but blank is treated the same as absent, not as "override with
        // empty string".
        var azd = new Dictionary<string, string> { ["PERSONAS_DIR"] = "" };
        string? dir = PersonaCatalogEnvResolver.ResolvePersonasDir(
            null, name => name == "PERSONAS_DIR" ? "/env/personas" : null, azd);
        Assert.Equal("/env/personas", dir);
    }

    [Fact]
    public void ResolveEnabledIds_ReturnsNull_WhenNothingIsSet()
    {
        Assert.Null(PersonaCatalogEnvResolver.ResolveEnabledIds(_ => null, s_emptyAzd));
    }

    [Fact]
    public void ResolveEnabledIds_ReturnsNull_WhenEnvironmentVariableIsEmpty()
    {
        Assert.Null(PersonaCatalogEnvResolver.ResolveEnabledIds(name => name == "PERSONAS" ? "" : null, s_emptyAzd));
    }

    [Fact]
    public void ResolveEnabledIds_SplitsTrimsAndDropsEmptyEntries()
    {
        IReadOnlyList<string>? ids = PersonaCatalogEnvResolver.ResolveEnabledIds(
            name => name == "PERSONAS" ? " alpha, bravo ,, mike " : null, s_emptyAzd);
        Assert.Equal(["alpha", "bravo", "mike"], ids);
    }

    [Fact]
    public void ResolveEnabledIds_AzdEnvValue_BeatsEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["PERSONAS"] = "mike" };
        IReadOnlyList<string>? ids = PersonaCatalogEnvResolver.ResolveEnabledIds(name => name == "PERSONAS" ? "alpha" : null, azd);
        Assert.Equal(["mike"], ids);
    }

    [Fact]
    public void ResolveEnabledIds_IgnoresAnEmptyAzdValue_FallingBackToTheEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["PERSONAS"] = "" };
        IReadOnlyList<string>? ids = PersonaCatalogEnvResolver.ResolveEnabledIds(name => name == "PERSONAS" ? "alpha" : null, azd);
        Assert.Equal(["alpha"], ids);
    }
}

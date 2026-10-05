namespace SearchIndexIngestor.Tests;

/// <summary>
/// Unit tests for <see cref="PersonaCatalogEnvResolver"/>'s <c>PERSONAS</c>/<c>PERSONAS_DIR</c>
/// precedence (Rick's review, item 2a/round 2) -- same shape as
/// SearchEndpointResolverTests.cs/OpenAiSettingsResolverTests.cs for its sibling resolvers.
/// </summary>
public sealed class PersonaCatalogEnvResolverTests
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAzd = new Dictionary<string, string>();

    [Fact]
    public void ResolvePersonasDir_ReturnsNull_WhenNothingIsSet()
    {
        Assert.Null(PersonaCatalogEnvResolver.ResolvePersonasDir(null, _ => null, EmptyAzd));
    }

    [Fact]
    public void ResolvePersonasDir_UsesEnvironmentVariable_WhenSet()
    {
        var dir = PersonaCatalogEnvResolver.ResolvePersonasDir(
            null, name => name == "PERSONAS_DIR" ? "/env/personas" : null, EmptyAzd);
        Assert.Equal("/env/personas", dir);
    }

    [Fact]
    public void ResolvePersonasDir_AzdEnvValue_BeatsEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["PERSONAS_DIR"] = "/azd/personas" };
        var dir = PersonaCatalogEnvResolver.ResolvePersonasDir(
            null, name => name == "PERSONAS_DIR" ? "/env/personas" : null, azd);
        Assert.Equal("/azd/personas", dir);
    }

    [Fact]
    public void ResolvePersonasDir_Flag_BeatsBothAzdAndEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["PERSONAS_DIR"] = "/azd/personas" };
        var dir = PersonaCatalogEnvResolver.ResolvePersonasDir(
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
        var dir = PersonaCatalogEnvResolver.ResolvePersonasDir(
            null, name => name == "PERSONAS_DIR" ? "/env/personas" : null, azd);
        Assert.Equal("/env/personas", dir);
    }

    [Fact]
    public void ResolveEnabledIds_ReturnsNull_WhenNothingIsSet()
    {
        Assert.Null(PersonaCatalogEnvResolver.ResolveEnabledIds(_ => null, EmptyAzd));
    }

    [Fact]
    public void ResolveEnabledIds_ReturnsNull_WhenEnvironmentVariableIsEmpty()
    {
        Assert.Null(PersonaCatalogEnvResolver.ResolveEnabledIds(name => name == "PERSONAS" ? "" : null, EmptyAzd));
    }

    [Fact]
    public void ResolveEnabledIds_SplitsTrimsAndDropsEmptyEntries()
    {
        var ids = PersonaCatalogEnvResolver.ResolveEnabledIds(
            name => name == "PERSONAS" ? " alpha, bravo ,, mike " : null, EmptyAzd);
        Assert.Equal(["alpha", "bravo", "mike"], ids);
    }

    [Fact]
    public void ResolveEnabledIds_AzdEnvValue_BeatsEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["PERSONAS"] = "mike" };
        var ids = PersonaCatalogEnvResolver.ResolveEnabledIds(name => name == "PERSONAS" ? "alpha" : null, azd);
        Assert.Equal(["mike"], ids);
    }

    [Fact]
    public void ResolveEnabledIds_IgnoresAnEmptyAzdValue_FallingBackToTheEnvironmentVariable()
    {
        var azd = new Dictionary<string, string> { ["PERSONAS"] = "" };
        var ids = PersonaCatalogEnvResolver.ResolveEnabledIds(name => name == "PERSONAS" ? "alpha" : null, azd);
        Assert.Equal(["alpha"], ids);
    }
}

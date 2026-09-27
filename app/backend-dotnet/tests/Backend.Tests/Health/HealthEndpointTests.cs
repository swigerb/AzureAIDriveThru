using Backend.Health;
using Backend.Personas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Backend.Tests.Health;

/// <summary>Direct-invoke tests for HealthEndpoint.Handle -- the /health JSON shape and status
/// code, without spinning up a real host (the conformance suite's HealthEndpointTests cover the
/// over-the-wire behaviour once DotnetBackendLauncher exists).</summary>
public sealed class HealthEndpointTests
{
    [Fact]
    public void AllChecksPassed_Returns200Healthy()
    {
        var checks = new StartupChecks();
        checks.Pass("env_vars");
        checks.Pass("personas_loaded");
        checks.Pass("prompts_loaded");

        var result = Assert.IsType<JsonHttpResult<Dictionary<string, object?>>>(HealthEndpoint.Handle(checks, catalog: null));

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("healthy", result.Value!["status"]);
    }

    [Fact]
    public void NotAllChecksPassed_Returns503Unhealthy()
    {
        var checks = new StartupChecks();
        checks.Pass("env_vars");
        // personas_loaded / prompts_loaded left unset.

        var result = Assert.IsType<JsonHttpResult<Dictionary<string, object?>>>(HealthEndpoint.Handle(checks, catalog: null));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal("unhealthy", result.Value!["status"]);
    }

    [Fact]
    public void WithCatalog_IncludesPersonaIds()
    {
        var checks = new StartupChecks();
        checks.Pass("env_vars");
        checks.Pass("personas_loaded");
        checks.Pass("prompts_loaded");
        var catalog = PersonaCatalog.Load();

        var result = Assert.IsType<JsonHttpResult<Dictionary<string, object?>>>(HealthEndpoint.Handle(checks, catalog));

        Assert.Equal(catalog.Ids, result.Value!["personas"]);
    }

    [Fact]
    public void WithoutCatalog_OmitsPersonasKey()
    {
        var checks = new StartupChecks();

        var result = Assert.IsType<JsonHttpResult<Dictionary<string, object?>>>(HealthEndpoint.Handle(checks, catalog: null));

        Assert.False(result.Value!.ContainsKey("personas"));
    }
}

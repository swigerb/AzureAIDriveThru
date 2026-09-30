using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Tests.TestSupport;

namespace Backend.Tests.Models;

/// <summary>ModelCatalog.FromConfig/ValidatePersonaDefaults tests (issue #75, design doc section
/// 7.2). config.yaml's real, shared `models.catalog` (4 entries) settled the shape as a top-level
/// list of mappings under `models:` -- these tests assert against that real content directly
/// rather than only synthetic fixtures, so a future edit to config.yaml's catalog is caught here
/// too.</summary>
public sealed class ModelCatalogTests
{
    [Fact]
    public void RealSharedConfig_HasFourCatalogueEntries()
    {
        var config = AppConfig.Load();

        var catalog = ModelCatalog.FromConfig(config);

        Assert.Equal(
            new[] { "gpt-5-mini", "gpt-realtime-2.1", "gpt-realtime-mini", "phi-4" },
            catalog.Ids);

        var realtime = catalog.Get("gpt-realtime-2.1");
        Assert.Equal("realtime", realtime.Pipeline);
        Assert.Equal("GPT Realtime 2.1", realtime.Label);
        Assert.True(realtime.Reasoning);

        var realtimeMini = catalog.Get("gpt-realtime-mini");
        Assert.False(realtimeMini.Reasoning);

        var cascade = catalog.Get("gpt-5-mini");
        Assert.Equal("cascade", cascade.Pipeline);
        Assert.True(cascade.ToolCalling);
    }

    [Fact]
    public void RealSharedConfig_IsCataloguedForChecksExactPipeline()
    {
        var catalog = ModelCatalog.FromConfig(AppConfig.Load());

        Assert.True(catalog.IsCataloguedFor("gpt-realtime-2.1", "realtime"));
        Assert.False(catalog.IsCataloguedFor("gpt-realtime-2.1", "cascade"));
        Assert.False(catalog.IsCataloguedFor("unknown-model", "realtime"));
    }

    [Fact]
    public void Get_UnknownModel_ThrowsWithKnownIdsListed()
    {
        var catalog = ModelCatalog.FromConfig(AppConfig.Load());

        var exc = Assert.Throws<KeyNotFoundException>(() => catalog.Get("does-not-exist"));
        Assert.Contains("does-not-exist", exc.Message);
        Assert.Contains("gpt-realtime-2.1", exc.Message);
    }

    [Fact]
    public void AbsentModelsSection_IsAnEmptyCatalog_NotAnError()
    {
        var path = WriteTempConfig("""
            model: {}
            business_rules: {}
            cache: {}
            audio: {}
            connection: {}
            """);

        var catalog = ModelCatalog.FromConfig(AppConfig.Load(path));

        Assert.Empty(catalog.Ids);
        Assert.False(catalog.Contains("anything"));
    }

    [Fact]
    public void ModelsCatalog_IsAListOfMappings_NotADictionary()
    {
        var path = WriteTempConfig("""
            model: {}
            business_rules: {}
            cache: {}
            audio: {}
            connection: {}
            models:
              catalog:
                gpt-4o-realtime: {}
            """);

        var exc = Assert.Throws<ModelValidationException>(() => ModelCatalog.FromConfig(AppConfig.Load(path)));
        Assert.Contains("must be a list", exc.Message);
    }

    [Fact]
    public void CatalogEntry_MissingRequiredField_Throws()
    {
        var path = WriteTempConfig("""
            model: {}
            business_rules: {}
            cache: {}
            audio: {}
            connection: {}
            models:
              catalog:
                - { id: gpt-4o-realtime, pipeline: realtime }
            """);

        var exc = Assert.Throws<ModelValidationException>(() => ModelCatalog.FromConfig(AppConfig.Load(path)));
        Assert.Contains("label", exc.Message);
    }

    [Fact]
    public void CatalogEntry_UnknownPipeline_Throws()
    {
        var path = WriteTempConfig("""
            model: {}
            business_rules: {}
            cache: {}
            audio: {}
            connection: {}
            models:
              catalog:
                - { id: some-model, pipeline: quantum, label: "Some Model" }
            """);

        var exc = Assert.Throws<ModelValidationException>(() => ModelCatalog.FromConfig(AppConfig.Load(path)));
        Assert.Contains("pipeline", exc.Message);
    }

    [Fact]
    public void CatalogEntry_DuplicateId_Throws()
    {
        var path = WriteTempConfig("""
            model: {}
            business_rules: {}
            cache: {}
            audio: {}
            connection: {}
            models:
              catalog:
                - { id: dup, pipeline: realtime, label: "One" }
                - { id: dup, pipeline: realtime, label: "Two" }
            """);

        var exc = Assert.Throws<ModelValidationException>(() => ModelCatalog.FromConfig(AppConfig.Load(path)));
        Assert.Contains("duplicate", exc.Message);
    }

    [Fact]
    public void DeploymentMap_MapsIdsToDeploymentNames()
    {
        var config = AppConfig.Load();
        var env = new Dictionary<string, string>
        {
            ["AZURE_AI_MODEL_DEPLOYMENTS"] = """{"gpt-realtime-2.1": "my-deployment"}""",
        };

        var catalog = ModelCatalog.FromConfig(config, env);

        Assert.True(catalog.IsDeployed("gpt-realtime-2.1"));
        Assert.Equal("my-deployment", catalog.DeploymentFor("gpt-realtime-2.1"));
        Assert.False(catalog.IsDeployed("gpt-realtime-mini"));
    }

    [Fact]
    public void DeploymentMap_MalformedJson_Throws()
    {
        var config = AppConfig.Load();
        var env = new Dictionary<string, string> { ["AZURE_AI_MODEL_DEPLOYMENTS"] = "not json" };

        Assert.Throws<ModelValidationException>(() => ModelCatalog.FromConfig(config, env));
    }

    [Fact]
    public void IsSelectable_RequiresBothCatalogAndDeployment()
    {
        var config = AppConfig.Load();
        var env = new Dictionary<string, string>
        {
            ["AZURE_AI_MODEL_DEPLOYMENTS"] = """{"gpt-realtime-2.1": "my-deployment"}""",
        };

        var catalog = ModelCatalog.FromConfig(config, env);

        Assert.True(catalog.IsSelectable("gpt-realtime-2.1", "realtime"));
        // Catalogued for realtime but not deployed -- not selectable.
        Assert.False(catalog.IsSelectable("gpt-realtime-mini", "realtime"));
        // Deployed but for the wrong pipeline -- not selectable.
        Assert.False(catalog.IsSelectable("gpt-realtime-2.1", "cascade"));
    }

    [Fact]
    public void ValidatePersonaDefaults_EnabledPersonasOwnDefaultMustBeCatalogued()
    {
        using var fixture = new PersonaPackFixture();
        var catalog = PersonaCatalog.Load(personasDir: fixture.PersonasDir);
        var modelCatalog = ModelCatalog.FromConfig(AppConfig.Load());

        // sonic's real persona.json declares gpt-realtime-2.1 as its realtime default, which IS
        // catalogued for the realtime pipeline in the real config.yaml -- must not throw.
        modelCatalog.ValidatePersonaDefaults(catalog);
    }

    [Fact]
    public void ValidatePersonaDefaults_UncataloguedDefault_Throws()
    {
        var path = WriteTempConfig("""
            model: {}
            business_rules: {}
            cache: {}
            audio: {}
            connection: {}
            models:
              catalog:
                - { id: some-other-model, pipeline: realtime, label: "Other" }
            """);
        var modelCatalog = ModelCatalog.FromConfig(AppConfig.Load(path));

        using var fixture = new PersonaPackFixture();
        var catalog = PersonaCatalog.Load(personasDir: fixture.PersonasDir);

        var exc = Assert.Throws<ModelValidationException>(() => modelCatalog.ValidatePersonaDefaults(catalog));
        Assert.Contains("sonic", exc.Message);
        Assert.Contains("realtime", exc.Message);
    }

    private static string WriteTempConfig(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "beth-model-catalog-" + Guid.NewGuid().ToString("n") + ".yaml");
        File.WriteAllText(path, content);
        return path;
    }
}

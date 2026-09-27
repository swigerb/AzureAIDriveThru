using Backend.Configuration;
using Backend.Models;

namespace Backend.Tests.Models;

/// <summary>ModelCatalog.FromConfig tests. config.yaml has no models.catalog section today
/// (Python's own #75 -- the design doc's origin for this section -- is still open), so the
/// primary case this wave must prove is graceful absence, not a fully populated catalog.</summary>
public sealed class ModelCatalogTests
{
    [Fact]
    public void RealSharedConfig_HasNoCatalogYet_ReturnsEmpty()
    {
        var config = AppConfig.Load();

        var catalog = ModelCatalog.FromConfig(config);

        Assert.Empty(catalog.Ids);
        Assert.False(catalog.Contains("anything"));
    }

    [Fact]
    public void TopLevelModelsCatalogSection_IsRead()
    {
        var path = WriteTempConfig("""
            model:
              foo: bar
            business_rules: {}
            cache: {}
            audio: {}
            connection: {}
            models:
              catalog:
                gpt-4o-realtime: {}
                claude-sonnet: {}
            """);

        var catalog = ModelCatalog.FromConfig(AppConfig.Load(path));

        Assert.Equal(2, catalog.Ids.Count);
        Assert.True(catalog.Contains("gpt-4o-realtime"));
        Assert.True(catalog.Contains("claude-sonnet"));
    }

    [Fact]
    public void NestedModelDotCatalogSection_IsRead()
    {
        var path = WriteTempConfig("""
            model:
              catalog:
                gpt-4o-realtime: {}
            business_rules: {}
            cache: {}
            audio: {}
            connection: {}
            """);

        var catalog = ModelCatalog.FromConfig(AppConfig.Load(path));

        Assert.Single(catalog.Ids);
        Assert.True(catalog.Contains("gpt-4o-realtime"));
    }

    private static string WriteTempConfig(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "beth-model-catalog-" + Guid.NewGuid().ToString("n") + ".yaml");
        File.WriteAllText(path, content);
        return path;
    }
}

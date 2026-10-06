using SearchIndexRequestBuilder;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// Unit tests for <see cref="PersonaIngestPlanBuilder"/> -- the <c>build_plan</c> port
/// (setup_search_index.py lines 392-410).
/// </summary>
public sealed class PersonaIngestPlanBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"squanchy-persona-ingest-plan-{Guid.NewGuid():n}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Build_Throws_WhenMenuFileIsMissing()
    {
        var persona = new EnabledPersonaDiscovery.DiscoveredPersona(
            "alpha", "alpha-index", Path.Combine(_dir, "does-not-exist.json"));

        var ex = Assert.Throws<InvalidOperationException>(() => PersonaIngestPlanBuilder.Build(persona));
        Assert.Contains("alpha", ex.Message);
        Assert.Contains("menu data file not found", ex.Message);
    }

    [Fact]
    public void Build_ReturnsPlan_WithOneDocumentAndTextPerMenuItem_InFileOrder()
    {
        Directory.CreateDirectory(_dir);
        var menuPath = Path.Combine(_dir, "menuItems.json");
        File.WriteAllText(
            menuPath,
            """
            {
              "menuItems": [
                {
                  "category": "Coffee",
                  "items": [
                    {"name": "Latte", "description": "Espresso and milk.", "sizes": []},
                    {"name": "Mocha", "description": "Espresso, milk, and chocolate.", "sizes": []}
                  ]
                }
              ]
            }
            """);
        var persona = new EnabledPersonaDiscovery.DiscoveredPersona("alpha", "alpha-index", menuPath);

        var plan = PersonaIngestPlanBuilder.Build(persona);

        Assert.Equal("alpha", plan.PersonaId);
        Assert.Equal("alpha-index", plan.IndexName);
        Assert.Equal(2, plan.DocumentCount);
        Assert.Equal(["coffee_latte", "coffee_mocha"], plan.Documents.Select(d => d["id"]!.GetValue<string>()));
        Assert.Equal(2, plan.TextsForEmbedding.Count);
        Assert.Contains("Latte", plan.TextsForEmbedding[0]);
    }
}

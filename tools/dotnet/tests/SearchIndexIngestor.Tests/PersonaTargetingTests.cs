using SearchIndexRequestBuilder;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// Faithful-port unit tests for setup_search_index.py's <c>resolve_target_personas</c> (lines
/// 365-389) -- see PersonaTargeting.cs's own remarks for the exact Python lines ported.
/// </summary>
public sealed class PersonaTargetingTests
{
    private static readonly IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> Catalog =
    [
        new("alpha", "alpha-index", "alpha/menu.json"),
        new("bravo", "bravo-index", "bravo/menu.json"),
        new("charlie", "charlie-index", "charlie/menu.json"),
    ];

    [Fact]
    public void ResolveTargetPersonas_ReturnsEveryPersona_WhenRequestedIsNull()
    {
        var result = PersonaTargeting.ResolveTargetPersonas(Catalog, null);
        Assert.Equal(["alpha", "bravo", "charlie"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_ReturnsEveryPersona_WhenRequestedIsEmpty()
    {
        var result = PersonaTargeting.ResolveTargetPersonas(Catalog, []);
        Assert.Equal(["alpha", "bravo", "charlie"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_ReturnsOnlyRequested_InRequestedOrder_NotCatalogOrder()
    {
        // --persona charlie --persona alpha (repeatable flag): order follows the FLAGS, not the
        // catalog's own sorted order -- matching Python's own
        // "[catalog.get(pid) for pid in ids]" (not sorted again).
        var result = PersonaTargeting.ResolveTargetPersonas(Catalog, ["charlie", "alpha"]);
        Assert.Equal(["charlie", "alpha"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_SplitsCommaSeparatedEntries()
    {
        var result = PersonaTargeting.ResolveTargetPersonas(Catalog, ["alpha,bravo"]);
        Assert.Equal(["alpha", "bravo"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_TrimsWhitespaceAndSkipsEmptyEntries()
    {
        var result = PersonaTargeting.ResolveTargetPersonas(Catalog, [" alpha , , bravo "]);
        Assert.Equal(["alpha", "bravo"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_Throws_WhenAnyRequestedIdIsUnknown()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PersonaTargeting.ResolveTargetPersonas(Catalog, ["alpha", "nonexistent"]));
        Assert.Contains("nonexistent", ex.Message);
        Assert.Contains("alpha", ex.Message);
    }

    [Fact]
    public void ResolveTargetPersonas_Throws_WithNoneText_WhenCatalogIsEmpty()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PersonaTargeting.ResolveTargetPersonas([], ["anything"]));
        Assert.Contains("(none)", ex.Message);
    }
}

using SearchIndexRequestBuilder;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// Faithful-port unit tests for setup_search_index.py's <c>resolve_target_personas</c> (lines
/// 365-389) -- see PersonaTargeting.cs's own remarks for the exact Python lines ported.
/// </summary>
public sealed class PersonaTargetingTests
{
    private static readonly IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> s_catalog =
    [
        new("alpha", "alpha-index", "alpha/menu.json"),
        new("bravo", "bravo-index", "bravo/menu.json"),
        new("charlie", "charlie-index", "charlie/menu.json"),
    ];

    [Fact]
    public void ResolveTargetPersonas_ReturnsEveryPersona_WhenRequestedIsNull()
    {
        IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> result = PersonaTargeting.ResolveTargetPersonas(s_catalog, null);
        Assert.Equal(["alpha", "bravo", "charlie"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_ReturnsEveryPersona_WhenRequestedIsEmpty()
    {
        IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> result = PersonaTargeting.ResolveTargetPersonas(s_catalog, []);
        Assert.Equal(["alpha", "bravo", "charlie"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_ReturnsOnlyRequested_InRequestedOrder_NotCatalogOrder()
    {
        // --persona charlie --persona alpha (repeatable flag): order follows the FLAGS, not the
        // catalog's own sorted order -- matching Python's own
        // "[catalog.get(pid) for pid in ids]" (not sorted again).
        IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> result = PersonaTargeting.ResolveTargetPersonas(s_catalog, ["charlie", "alpha"]);
        Assert.Equal(["charlie", "alpha"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_SplitsCommaSeparatedEntries()
    {
        IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> result = PersonaTargeting.ResolveTargetPersonas(s_catalog, ["alpha,bravo"]);
        Assert.Equal(["alpha", "bravo"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_TrimsWhitespaceAndSkipsEmptyEntries()
    {
        IReadOnlyList<EnabledPersonaDiscovery.DiscoveredPersona> result = PersonaTargeting.ResolveTargetPersonas(s_catalog, [" alpha , , bravo "]);
        Assert.Equal(["alpha", "bravo"], result.Select(p => p.PersonaId));
    }

    [Fact]
    public void ResolveTargetPersonas_Throws_WhenAnyRequestedIdIsUnknown()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PersonaTargeting.ResolveTargetPersonas(s_catalog, ["alpha", "nonexistent"]));
        Assert.Contains("nonexistent", ex.Message);
        Assert.Contains("alpha", ex.Message);
    }

    [Fact]
    public void ResolveTargetPersonas_Throws_WithNoneText_WhenCatalogIsEmpty()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PersonaTargeting.ResolveTargetPersonas([], ["anything"]));
        Assert.Contains("(none)", ex.Message);
    }
}

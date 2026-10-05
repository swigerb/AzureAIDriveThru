using Conformance.Fakes;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="MenuIndex.ResolveIndexPaths"/> (Rick's PR #108 second review,
/// required item C). Pure and process-free -- exercised against scratch temp directories with
/// hand-written persona.json stubs, never the repo's real personas/ folder, so these tests never
/// depend on how many real packs currently exist on disk.
/// </summary>
[Trait("Category", "Harness")]
[Trait("Dotnet", "n/a-harness")] // Issue #21: backend-agnostic harness self-test, never exercises app/backend or app/backend-dotnet.
public sealed class MenuIndexResolveIndexPathsTests
{
    private static string CreatePersona(string root, string personaId, string indexName)
    {
        var dir = Path.Combine(root, personaId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "persona.json"),
            $$"""{ "search": { "indexName": "{{indexName}}" } }""");
        return dir;
    }

    [Fact]
    public void Maps_each_persona_to_its_own_index_name_and_menu_items_json_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "menu-index-test-" + Guid.NewGuid());
        try
        {
            CreatePersona(root, "sonic", "sonic-menu-items");
            CreatePersona(root, "test-alpha", "test-alpha-menu-items");

            var result = MenuIndex.ResolveIndexPaths(root, ["sonic", "test-alpha"]);

            Assert.Equal(2, result.Count);
            Assert.Equal(Path.Combine(root, "sonic", "menu", "menuItems.json"), result["sonic-menu-items"]);
            Assert.Equal(Path.Combine(root, "test-alpha", "menu", "menuItems.json"), result["test-alpha-menu-items"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Rick's PR #108 second review, required item C: two DIFFERENT personas declaring the SAME
    /// search.indexName in their own persona.json used to silently overwrite the first persona's
    /// entry with the second's -- the fake would then answer that shared index with only the
    /// second persona's documents. Must throw instead, naming both persona ids and the shared
    /// index name.
    /// </summary>
    [Fact]
    public void Throws_when_two_different_personas_declare_the_same_index_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "menu-index-test-" + Guid.NewGuid());
        try
        {
            CreatePersona(root, "test-alpha", "shared-index");
            CreatePersona(root, "test-beta", "shared-index");

            var ex = Assert.Throws<InvalidOperationException>(
                () => MenuIndex.ResolveIndexPaths(root, ["test-alpha", "test-beta"]));

            Assert.Contains("shared-index", ex.Message);
            Assert.Contains("test-alpha", ex.Message);
            Assert.Contains("test-beta", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Does_not_throw_when_the_same_persona_id_is_resolved_twice_with_the_same_index_name()
    {
        // Defence in depth: re-resolving the SAME persona id (e.g. a caller passing a
        // duplicate-but-identical id list) must not be mistaken for a genuine cross-persona
        // collision -- only two DIFFERENT persona ids sharing one index name is the bug.
        var root = Path.Combine(Path.GetTempPath(), "menu-index-test-" + Guid.NewGuid());
        try
        {
            CreatePersona(root, "sonic", "sonic-menu-items");

            var result = MenuIndex.ResolveIndexPaths(root, ["sonic", "sonic"]);

            Assert.Single(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="ConformancePersonas"/> (issue #76 groundwork: the harness's persona
/// dimension). Pure and process/filesystem-free wherever the class itself is pure --
/// <see cref="ConformancePersonas.DiscoverFromDisk(string)"/>'s testable overload is exercised
/// against a scratch temp directory instead of the real repo's personas/ folder, so these tests
/// never depend on how many persona packs currently exist on disk.
/// </summary>
[Trait("Category", "Harness")]
[Trait("Dotnet", "n/a-harness")] // Issue #21: backend-agnostic harness self-test, never exercises app/backend or app/backend-dotnet.
public sealed class ConformancePersonasTests
{
    [Fact]
    public void ResolveEnabled_uses_explicit_env_value_when_set_trimming_and_dropping_empties()
    {
        var result = ConformancePersonas.ResolveEnabled(
            " sonic , mcdonalds ,, dunkin ", () => throw new InvalidOperationException("must not discover when env var is set"));

        Assert.Equal(["sonic", "mcdonalds", "dunkin"], result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveEnabled_falls_back_to_disk_discovery_when_env_value_is_null_or_blank(string? envValue)
    {
        var result = ConformancePersonas.ResolveEnabled(envValue, () => ["dunkin", "mcdonalds", "sonic"]);

        Assert.Equal(["dunkin", "mcdonalds", "sonic"], result);
    }

    [Fact]
    public void ResolveEnabled_falls_back_to_a_comma_only_string_as_disk_discovery_since_no_ids_survive_trimming()
    {
        // ",, ,," has no non-empty entries after trimming -- must behave like the env var was
        // never set, not return an empty enabled-persona list.
        var result = ConformancePersonas.ResolveEnabled(",, ,,", () => ["mcdonalds"]);

        Assert.Equal(["mcdonalds"], result);
    }

    [Fact]
    public void ResolveEnabled_falls_back_to_sonic_when_env_unset_and_disk_discovery_finds_nothing()
    {
        var result = ConformancePersonas.ResolveEnabled(null, () => []);

        Assert.Equal([ConformancePersonas.DefaultPersonaId], result);
    }

    [Fact]
    public void ResolveDefault_uses_the_override_when_it_names_an_enabled_persona()
    {
        Assert.Equal("mcdonalds", ConformancePersonas.ResolveDefault("mcdonalds", ["dunkin", "mcdonalds", "sonic"]));
    }

    [Fact]
    public void ResolveDefault_falls_back_to_sonic_when_override_does_not_name_an_enabled_persona()
    {
        Assert.Equal("sonic", ConformancePersonas.ResolveDefault("not-enabled", ["dunkin", "sonic"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDefault_falls_back_to_sonic_when_override_is_null_or_blank_and_sonic_is_enabled(string? overridePersonaId)
    {
        Assert.Equal("sonic", ConformancePersonas.ResolveDefault(overridePersonaId, ["dunkin", "sonic"]));
    }

    [Fact]
    public void ResolveDefault_falls_back_to_the_first_enabled_persona_when_sonic_is_not_enabled()
    {
        Assert.Equal("dunkin", ConformancePersonas.ResolveDefault(null, ["dunkin", "mcdonalds"]));
    }

    [Fact]
    public void ResolveDefault_falls_back_to_sonic_even_when_enabled_list_is_empty()
    {
        // Defence in depth: ResolveEnabled itself should never return an empty list, but
        // ResolveDefault should not throw/index-out-of-range if it somehow did.
        Assert.Equal("sonic", ConformancePersonas.ResolveDefault(null, []));
    }

    [Fact]
    public void EnsureIncluded_is_a_no_op_when_override_is_null_or_blank()
    {
        IReadOnlyList<string> enabled = ["sonic"];
        Assert.Same(enabled, ConformancePersonas.EnsureIncluded(enabled, null));
        Assert.Same(enabled, ConformancePersonas.EnsureIncluded(enabled, ""));
        Assert.Same(enabled, ConformancePersonas.EnsureIncluded(enabled, "   "));
    }

    [Fact]
    public void EnsureIncluded_is_a_no_op_when_override_is_already_enabled()
    {
        IReadOnlyList<string> enabled = ["sonic", "dunkin"];
        Assert.Same(enabled, ConformancePersonas.EnsureIncluded(enabled, "dunkin"));
    }

    [Fact]
    public void EnsureIncluded_adds_and_ordinally_sorts_a_previously_unenabled_override()
    {
        var result = ConformancePersonas.EnsureIncluded(["sonic"], "mcdonalds");

        Assert.Equal(["mcdonalds", "sonic"], result);
    }

    [Fact]
    public void DiscoverFromDisk_returns_empty_when_the_directory_does_not_exist()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "conformance-personas-test-" + Guid.NewGuid());

        Assert.Empty(ConformancePersonas.DiscoverFromDisk(missingDir));
    }

    [Fact]
    public void DiscoverFromDisk_returns_only_subfolders_containing_persona_json_sorted_ordinally()
    {
        var root = Path.Combine(Path.GetTempPath(), "conformance-personas-test-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sonic"));
            File.WriteAllText(Path.Combine(root, "sonic", "persona.json"), "{}");

            Directory.CreateDirectory(Path.Combine(root, "dunkin"));
            File.WriteAllText(Path.Combine(root, "dunkin", "persona.json"), "{}");

            // No persona.json -- an in-progress/incomplete pack folder must not count as discovered.
            Directory.CreateDirectory(Path.Combine(root, "mcdonalds-wip"));

            var result = ConformancePersonas.DiscoverFromDisk(root);

            Assert.Equal(["dunkin", "sonic"], result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

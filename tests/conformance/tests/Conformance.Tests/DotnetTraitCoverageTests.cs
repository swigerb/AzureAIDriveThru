using System.Linq;
using System.Reflection;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #76, Rick's PR #101 review item 1: replaces the hard-coded dotnet CI leg filter (a
/// <c>FullyQualifiedName~ClassNamePart</c> substring match, which any future test class whose
/// name happens to contain one of those substrings would silently join) with an explicit
/// <c>[Trait("Dotnet", "ready")]</c> on exactly the scenarios docs/dotnet_mapping.md documents as
/// green against the C# skeleton today (11 scenarios). The dotnet CI leg now runs
/// <c>--filter "Dotnet=ready&amp;Category!=Browser"</c> instead.
///
/// This test is the guard that the tagged count can't silently shrink: a PR that removes or
/// renames a tagged scenario without adding a replacement fails here, instead of just quietly
/// running fewer scenarios in a CI log nobody reads. C# PRs #13-#16 are expected to *grow* this
/// count as more of the skeleton gets a real pipeline wired in (docs/dotnet_mapping.md) -- they do
/// that by adding the trait directly in their own test files, with no workflow/CI edit required
/// (the dotnet leg's filter already covers any newly tagged scenario for free).
/// </summary>
public sealed class DotnetTraitCoverageTests
{
    private const string TraitName = "Dotnet";
    private const string TraitValue = "ready";

    [Fact]
    public void At_least_11_scenarios_are_tagged_dotnet_ready()
    {
        var count = CountDotnetReadyTestMethods();

        Assert.True(count >= 11,
            $"Expected at least 11 test method(s) tagged [Trait(\"{TraitName}\", \"{TraitValue}\")] " +
            $"(the dotnet leg's `--filter \"{TraitName}={TraitValue}&Category!=Browser\"` baseline, " +
            $"docs/dotnet_mapping.md), but found {count}. If a tagged scenario was removed or renamed " +
            "without a replacement, the dotnet CI leg silently lost coverage.");
    }

    /// <summary>
    /// Counts every <c>[Fact]</c>/<c>[Theory]</c> test *method* (a <c>[Theory]</c> with N
    /// <c>[InlineData]</c> rows still counts once here, same as the
    /// <c>FullyQualifiedName</c>-based filter this replaces -- both count distinct methods, not
    /// distinct data rows) whose effective Dotnet trait is "ready", combining method-level and
    /// class-level <c>[Trait]</c> attributes the same way xunit's own trait-based filtering does:
    /// a class-level trait applies to every test method declared in that class.
    /// </summary>
    private static int CountDotnetReadyTestMethods()
    {
        var assembly = typeof(DotnetTraitCoverageTests).Assembly;
        var count = 0;

        foreach (var type in assembly.GetTypes())
        {
            var classHasTrait = HasDotnetReadyTrait(type.GetCustomAttributes<TraitAttribute>(inherit: true));

            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!method.IsDefined(typeof(FactAttribute), inherit: true))
                {
                    continue;
                }

                var methodHasTrait = HasDotnetReadyTrait(method.GetCustomAttributes<TraitAttribute>(inherit: true));
                if (classHasTrait || methodHasTrait)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool HasDotnetReadyTrait(IEnumerable<TraitAttribute> traits) =>
        traits.Any(t => t.Name == TraitName && t.Value == TraitValue);
}

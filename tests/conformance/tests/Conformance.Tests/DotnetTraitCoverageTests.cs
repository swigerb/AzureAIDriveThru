using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #76, Rick's PR #101 review item 1: replaces the hard-coded dotnet CI leg filter (a
/// <c>FullyQualifiedName~ClassNamePart</c> substring match, which any future test class whose
/// name happens to contain one of those substrings would silently join) with an explicit
/// <c>[Trait("Dotnet", "ready")]</c> on exactly the scenarios docs/dotnet_mapping.md documents as
/// green against the C# skeleton (167 distinct tagged test *methods* as of PR #149 round 2 -- several
/// <c>[Theory]</c> methods have multiple <c>[InlineData]</c>/<c>[MemberData]</c> rows each, so
/// <c>dotnet test</c>'s own pass count for the same filter is higher than 167 result rows; this test
/// counts methods, matching the <c>FullyQualifiedName</c> filter it replaced). The dotnet CI leg now runs
/// <c>--filter "Dotnet=ready&amp;Category!=Browser"</c> instead.
///
/// This test is the guard that the tagged count can't silently shrink: a PR that removes or
/// renames a tagged scenario without adding a replacement fails here, instead of just quietly
/// running fewer scenarios in a CI log nobody reads. C# PRs #13-#16 are expected to *grow* this
/// count as more of the skeleton gets a real pipeline wired in (docs/dotnet_mapping.md) -- they do
/// that by adding the trait directly in their own test files, with no workflow/CI edit required
/// (the dotnet leg's filter already covers any newly tagged scenario for free).
///
/// Issue #143/ADR-002 (R10, Rick's PR #158 round 1 review): tagged nine <c>Scenarios/Auth</c>
/// auth-row test classes. Five of them (<c>AuthModeLaunchTests</c>, <c>AuthRowLoggingTests</c>,
/// <c>AuthRowRealtimeTokenTests</c>, <c>AuthRowRestTokenTests</c>, <c>AuthRowSpecialCaseTests</c> --
/// 18 methods) route every test method through <c>RunAuthRowAsync</c>/<c>AssertFailsFastAsync</c>,
/// which call <c>Assert.Skip</c> (via <see cref="Conformance.Harness.AuthRowCapability.ShouldSkipCurrentBackend"/>)
/// before any backend interaction: they show up as skipped, not run, on the dotnet leg until issue
/// #147 flips <c>DotnetEnforcesAuth</c>. Tagging them was safe (skipped tests can't fail the dotnet
/// leg), but <see cref="AuthRowGatedTypeNames"/> excludes them from THIS floor: counting a
/// skip-only method here would let a real regression (removing genuinely-passing coverage
/// elsewhere) hide behind these always-skipped rows staying tagged, which defeats the point of a
/// coverage floor. The other four Auth classes -- <c>AuthRowCasesTests</c>/
/// <c>AuthRowRealtimeAssertionsTests</c> (pure token-minting/assertion-helper unit tests, no
/// backend, never gated) and row 16's <c>DevelopmentPassThroughUnsetModeTests</c>/
/// <c>DevelopmentPassThroughExplicitModeTests</c> (real, ungated, already-passing-today
/// pass-through behaviour) -- count normally, since they run and assert something real against the
/// dotnet leg today.
///
/// PR #158 CI-trigger fix (dev merge, bringing in #149's floor of 167 and #156): the merged tree's
/// raw tagged-method count (before excluding the skip-gated set below) is 196 (167 real + 29 from
/// R10's tagging); subtracting the 18 skip-gated methods above gives the 178 floor below.
///
/// Issue 165: the new Breakfast/Lunch menu-mode conformance scenarios
/// (<c>Scenarios/Ordering/MenuModeConformanceTests.cs</c>) add five tagged, ungated methods
/// (mode switch x2, out-of-mode rejection, search filter, packs-without-modes-unaffected),
/// verified green against both backends -- raising the floor from 178 to 183.
///
/// Issue 165 round 2 (Rick's PR #166 round-1 review, required items 5 and 7):
/// <c>Scenarios/Ordering/MenuModeRejectionConformanceTests.cs</c> adds five more tagged, ungated
/// methods -- unrecognized/empty/repeated `?mode=` all rejected with a real pre-upgrade HTTP 400
/// (closing the gap Rick flagged: "the Python 400 test was vacuous... and C# had no test of the
/// 400 at all"), an omitted `?mode=` defaulting to lunch, and a log-capture pin proving the raw
/// rejected value never reaches either backend's own logs verbatim -- verified green against both
/// Issue 165 round 2 (Rick's PR #166 round-1 review, required item 6): search vs. add-time
/// semantics for a period-less item within a dayparts pack now agree in both directions --
/// <c>Scenarios/Ordering/MenuModeConformanceTests.cs</c> adds one more tagged, ungated
/// <c>[Theory]</c> method (`Periodless_item_can_be_added_in_either_mode_a_dayparts_pack_supports`,
/// two <c>[InlineData]</c> rows: breakfast and lunch) proving "Delta Burger" -- a genuine
/// dayparts-pack item with no `menuPeriod` of its own -- is addable over the wire in either mode,
/// matching the filter-string fix's own admission of period-less items -- raising the floor from
/// 188 to 189.
///
/// Issue #164 (Rick's PR #167 round-3 review, required item 12): PR #167 adds one scenario,
/// <c>PersonaDiscoveryConformanceTests.Api_persona_detail_pins_tax_rate_and_ui_blocks_against_disk</c>
/// (R7, commit 48edade). It is tagged <c>[Trait("Dotnet", "ready")]</c> and is not skip-gated,
/// so it raises the floor 189 to 190. The dotnet leg executes 190 distinct passing methods, none
/// of them in the five gated Auth classes (TRX: 485 passed, 61 not executed). A reflection probe
/// agrees (a floor of 191 fails with "but found 190").
///
/// Issue #170: fixes a live production bug (a bound-persona session's client `session.update`
/// rebuilt the upstream session without that persona's own system prompt, falling back to the
/// deployment default). Adds <c>PersonaSessionUpdateInstructionsConformanceTests.cs</c>'s two
/// Theory methods (<c>RealPackPersonaSessionUpdateConformanceTests</c> and
/// <c>FixturePackPersonaSessionUpdateConformanceTests</c>, both
/// <c>Client_session_update_carries_the_bound_personas_own_instructions</c>) -- generic,
/// brand-agnostic coverage, for every discovered persona pack, that the forwarded client-update
/// session carries that SAME pack's own instructions and none of the others', closing the exact
/// gap that let #170 ship (<c>SmokeSessionBootstrapTests</c> already proved the browser's
/// session.update is forwarded, but never asserted `instructions`). Both methods are tagged
/// <c>[Trait("Dotnet", "ready")]</c>, ungated, and verified green against both backends -- raising
/// the floor 190 to 192.
///
/// Issue #170 round 2 (Rick's PR #175 round-2 review, required item R1): the instructions check
/// above only ever covered the ORDINARY client session.update rebuild, never the REJECTED-update
/// fallback path (<c>RealtimeProcessor.HandleErrorAsync</c> --&gt;
/// <c>RealtimeSessionBuilder.BuildFallbackSessionUpdate</c>) -- Rick's own round-1 mutation
/// forcing the C# fallback onto the deployment default persona's prompt survived every existing
/// test, since every prior fallback scenario only ever ran on the single default-persona
/// connection. <c>PersonaSessionUpdateFallbackConformanceTests.cs</c>'s two Theory methods
/// (<c>RealPackPersonaSessionUpdateFallbackConformanceTests</c> and
/// <c>FixturePackPersonaSessionUpdateFallbackConformanceTests</c>, both
/// <c>Rejected_bootstrap_recovers_via_a_fallback_carrying_the_bound_personas_own_instructions</c>)
/// close that gap generically, for every discovered persona pack, on both legs: a scripted
/// rejection of the bootstrap session.update, asserting the FALLBACK's own `instructions` carry
/// that SAME pack's identity text and none of the others'. Both methods are tagged
/// <c>[Trait("Dotnet", "ready")]</c>, ungated, and verified green against both backends -- raising
/// the floor 192 to 194.
///
/// Issue #170 round 3 (Rick's PR #175 round-2 review, required item R4): the instructions-only
/// checks above never covered `session.tools[].description` -- every session's tool list (both
/// the realtime and cascade backends) was built once from the deployment default persona's own
/// `prompts/tool_schemas.yaml`, so a bound persona's own system prompt and menu were correct but
/// its tool descriptions still named the default persona's own brand and ticket/order-screen
/// name. <c>PersonaSessionUpdateToolsConformanceTests.cs</c>'s two Theory methods
/// (<c>RealPackPersonaSessionUpdateToolsConformanceTests</c> and
/// <c>FixturePackPersonaSessionUpdateToolsConformanceTests</c>, both
/// <c>Client_session_update_carries_the_bound_personas_own_tool_descriptions</c>) close that gap
/// generically, for every discovered persona pack, on both legs: the forwarded client-update
/// session's `search` tool description carries that SAME pack's own text and none of the others'.
/// Both methods are tagged <c>[Trait("Dotnet", "ready")]</c>, ungated, and verified green against
/// both backends -- raising the floor 194 to 196.
///
/// Issue #179: a guest's combo drink was resized by the model calling `remove &lt;item&gt;
/// &lt;old size&gt;` then `add &lt;item&gt; &lt;new size&gt;`; the `remove` didn't vacate the
/// combo slot it had been filling, so the following `add` created a standalone duplicate line
/// instead of resizing the combo's own drink. <c>ComboComponentResizeConformanceTests.cs</c> adds
/// two tagged, ungated <c>[Theory]</c> methods (<c>Discovered_pack_resizes_the_combo_drink_via_remove_then_add</c>,
/// reproducing the exact live sequence, and <c>Discovered_pack_resizes_the_combo_drink_via_explicit_modify</c>,
/// covering the new explicit resize action), each driven by <c>ComboBundleDiscovery</c> dynamically
/// discovering every real pack with a genuinely-open drinks slot (no brand names in the test file
/// itself) -- verified green against both backends for every real pack discovered on disk whose
/// own menu qualifies today (a pack with no bundle at all is naturally excluded) -- raising the floor
/// 196 to 198.
///
/// Issue #179 round 2 (#184, Rick's required items 1/2): the pricing model changed from a
/// stateful delta/upcharge to a pure, path-independent function of the final order, and a pack's
/// own `bundles.resizeRule` can now be `wholeBundleSize` (resizing ANY slot component cascades
/// into resizing the WHOLE bundle), which the generic per-component scenario above does not apply
/// to and now correctly excludes. <c>ComboComponentResizeConformanceTests.cs</c>'s pricing
/// assertion was fixed to the new flat total, and a new tagged, ungated <c>[Theory]</c> method,
/// <c>WholeBundleSizeResizeConformanceTests.Discovered_whole_bundle_size_pack_resizes_the_meal_and_relabels_its_slots</c>,
/// covers the `wholeBundleSize` mechanism generically (dynamically discovering any pack with that
/// rule from its own persona.json, no brand names) -- verified green against both backends, and
/// mutation-checked (dotnet leg) by temporarily reverting the bundle's own reprice-on-resize line
/// in OrderState.cs, confirming the new test fails -- raising the floor 198 to 199.
///
/// Issue #184 round 3 (Rick's review, item H): two new tagged, ungated Theory methods in
/// <c>ComboComponentResizeConformanceTests.cs</c> -- a path-independence check (ordering a size
/// up front totals identically to resizing into it later) and a two-bundle-instance check (a
/// resize lands on the instance that actually holds the named item, by identity, never an
/// arbitrary first match) -- verified green against both backends, raising the floor 199 to 201.
/// </summary>
public sealed class DotnetTraitCoverageTests
{
    private const string TraitName = "Dotnet";
    private const string TraitValue = "ready";

    /// <summary>
    /// Full names of the <c>Scenarios/Auth</c> test classes whose methods are unconditionally
    /// skip-gated (see the class doc above) -- excluded from <see cref="CountFloorEligibleDotnetReadyTestMethods"/>
    /// so this floor only ever counts methods that produce a real pass/fail signal on the dotnet
    /// leg today.
    /// </summary>
    private static readonly HashSet<string> AuthRowGatedTypeNames = new(StringComparer.Ordinal)
    {
        "Conformance.Tests.Scenarios.Auth.AuthModeLaunchTests",
        "Conformance.Tests.Scenarios.Auth.AuthRowLoggingTests",
        "Conformance.Tests.Scenarios.Auth.AuthRowRealtimeTokenTests",
        "Conformance.Tests.Scenarios.Auth.AuthRowRestTokenTests",
        "Conformance.Tests.Scenarios.Auth.AuthRowSpecialCaseTests",
    };

    [Fact]
    public void At_least_201_scenarios_are_tagged_dotnet_ready_and_not_skip_gated()
    {
        var count = CountFloorEligibleDotnetReadyTestMethods();

        Assert.True(count >= 201,
            $"Expected at least 201 test method(s) tagged [Trait(\"{TraitName}\", \"{TraitValue}\")] " +
            $"and not unconditionally skip-gated by AuthRowCapability (the dotnet leg's " +
            $"`--filter \"{TraitName}={TraitValue}&Category!=Browser\"` baseline, minus the five " +
            "skip-only Scenarios/Auth classes -- see this class's own doc comment; " +
            $"docs/dotnet_mapping.md), but found {count}. If a tagged scenario was removed or " +
            "renamed without a replacement, the dotnet CI leg silently lost coverage.");
    }

    /// <summary>
    /// Counts every <c>[Fact]</c>/<c>[Theory]</c> test *method* (a <c>[Theory]</c> with N
    /// <c>[InlineData]</c> rows still counts once here, same as the
    /// <c>FullyQualifiedName</c>-based filter this replaces -- both count distinct methods, not
    /// distinct data rows) whose effective Dotnet trait is "ready", combining method-level and
    /// class-level <c>[Trait]</c> attributes the same way xunit's own trait-based filtering does:
    /// a class-level trait applies to every test method declared in that class. Excludes any type
    /// listed in <see cref="AuthRowGatedTypeNames"/>: those methods are unconditionally
    /// <c>Assert.Skip</c>'d on the dotnet leg today (see this class's own doc comment), so they
    /// never contribute a real pass/fail signal and must not count toward the coverage floor.
    ///
    /// Issue #143/ADR-002 (R10): abstract types are skipped outright -- xunit never discovers an
    /// abstract class as a runnable test class in its own right, only its concrete subclasses --
    /// and each concrete subclass's own (non-<c>DeclaredOnly</c>) methods are walked so a
    /// <c>[Fact]</c> declared once on a shared abstract base (see
    /// <c>Scenarios.Auth.DevelopmentPassThroughTestsBase</c>, run twice over via its two sealed,
    /// separately-<c>[Trait]</c>-tagged, separately-fixtured subclasses) is credited once per
    /// concrete subclass that actually runs it -- matching how many real xunit test cases the
    /// dotnet leg's own <c>--filter</c> actually selects, not how many methods happen to be typed
    /// out once in source.
    /// </summary>
    private static int CountFloorEligibleDotnetReadyTestMethods()
    {
        var assembly = typeof(DotnetTraitCoverageTests).Assembly;
        var count = 0;

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || (type.FullName is not null && AuthRowGatedTypeNames.Contains(type.FullName)))
            {
                continue;
            }

            var classHasTrait = HasDotnetReadyTrait(type.GetCustomAttributes<TraitAttribute>(inherit: true));

            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
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

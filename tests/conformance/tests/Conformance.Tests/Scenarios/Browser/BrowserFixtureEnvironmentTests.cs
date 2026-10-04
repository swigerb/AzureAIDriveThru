using System.Reflection;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Browser;

/// <summary>
/// Issue #143/ADR-002 (R9) pin: asserts the Browser collection's nested backend fixture opts out
/// of Entra mode and launches "Not Production" on both backends, matching the frontend bundle's
/// own <c>VITE_AUTH_MODE=Development</c> build (scope item 4, persona-architecture.md 18.11).
/// Reads <see cref="BrowserConformanceFixture.BrowserTimersBackendFixture"/>'s protected
/// <c>Profile</c>/<c>UseEntraMode</c> overrides by reflection rather than instantiating it (which
/// would start a real Python process and both fakes), this only needs the static shape those
/// overrides produce, never a running backend.
///
/// Issue #21 (Browser-on-C#): tagged <c>Category=Browser</c>/<c>Dotnet=ready</c> -- it never starts
/// either backend (pure reflection over the fixture's static shape), so it is backend-agnostic by
/// construction and passes identically regardless of which launcher a real Browser fixture would
/// pick. Verified 3x locally with CONFORMANCE_BACKEND=dotnet (no flakes possible: no process, no
/// I/O, no timing).
/// </summary>
[Trait("Category", "Browser")]
[Trait("Dotnet", "ready")]
public sealed class BrowserFixtureEnvironmentTests
{
    private static readonly Type FixtureType =
        typeof(BrowserConformanceFixture).GetNestedType(
            "BrowserTimersBackendFixture", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "BrowserConformanceFixture.BrowserTimersBackendFixture not found by reflection.");

    private static object CreateFixture() =>
        Activator.CreateInstance(FixtureType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not construct BrowserTimersBackendFixture.");

    private static T GetProtectedProperty<T>(object instance, string name)
    {
        var property = typeof(ConformanceFixture).GetProperty(
            name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"ConformanceFixture.{name} not found by reflection.");
        return (T)property.GetValue(instance)!;
    }

    [Fact]
    public void Browser_fixture_disables_entra_mode()
    {
        var useEntraMode = GetProtectedProperty<bool>(CreateFixture(), "UseEntraMode");

        Assert.False(useEntraMode);
    }

    [Fact]
    public void Browser_fixture_profile_has_no_auth_mode_or_entra_keys()
    {
        var profile = GetProtectedProperty<BackendProfile>(CreateFixture(), "Profile");

        Assert.DoesNotContain("AUTH_MODE", profile.ExtraEnvironment.Keys);
        Assert.DoesNotContain(profile.ExtraEnvironment.Keys, key => key.StartsWith("ENTRA_", StringComparison.Ordinal));
    }

    [Fact]
    public void Browser_fixture_profile_is_not_production_on_either_launcher()
    {
        var profile = GetProtectedProperty<BackendProfile>(CreateFixture(), "Profile");

        Assert.Equal("false", profile.ExtraEnvironment["RUNNING_IN_PRODUCTION"]);
        Assert.Equal("Development", profile.ExtraEnvironment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Development", profile.ExtraEnvironment["DOTNET_ENVIRONMENT"]);
    }

    [Fact]
    public void Browser_fixture_profile_still_carries_browser_timer_budgets()
    {
        // Not Production shouldn't come at the cost of the real-browser timer headroom
        // BackendProfiles.BrowserTimers exists for, every one of its keys/values must still be
        // present unchanged.
        var profile = GetProtectedProperty<BackendProfile>(CreateFixture(), "Profile");

        foreach (var (key, value) in BackendProfiles.BrowserTimers.ExtraEnvironment)
        {
            Assert.Equal(value, profile.ExtraEnvironment[key]);
        }
    }
}

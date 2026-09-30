using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Issue #143/ADR-002, persona-architecture.md 18.11: "A DevelopmentPassThrough fixture
/// (non-Production, unconfigured) runs the mode rows and the Playwright UX runs." Overrides
/// <see cref="ConformanceFixture.UseEntraMode"/> to false, so no ENTRA_*/AUTH_MODE env is set at
/// all (the "AUTH_MODE unset" half of row 16), paired with
/// <see cref="BackendProfiles.DevelopmentPassThrough"/> for a "Not Production" environment on
/// either backend. Row 16's own tests (<c>DevelopmentPassThroughTests.cs</c>) assert the
/// pass-through behaviour; this fixture is also what Playwright's UX runs build their frontend
/// bundle against (<c>VITE_AUTH_MODE=Development</c>, matching this backend's own unconfigured,
/// non-Production shape).
/// </summary>
public sealed class DevelopmentPassThroughFixture : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.DevelopmentPassThrough;
    protected override bool UseEntraMode => false;
}

[CollectionDefinition(Name)]
public sealed class DevelopmentPassThroughCollection : ICollectionFixture<DevelopmentPassThroughFixture>
{
    public const string Name = "ConformanceDevelopmentPassThrough";
}

/// <summary>
/// Row 16's second variant -- persona-architecture.md 18.5: "`Development` | None | Not Production
/// | Development pass-through, the same as the row above" (i.e. explicit <c>AUTH_MODE=Development</c>
/// with no ids configured behaves identically to leaving AUTH_MODE unset). A separate dedicated
/// fixture/collection/process is needed (rather than asserting this on
/// <see cref="DevelopmentPassThroughFixture"/> too) because <c>AUTH_MODE</c> is read once at
/// backend startup, like every other env var this harness sets -- an already-running process
/// can't flip from "unset" to "Development" mid-suite.
/// </summary>
public sealed class DevelopmentPassThroughExplicitModeFixture : ConformanceFixture
{
    protected override BackendProfile Profile => BackendProfiles.DevelopmentPassThroughExplicitMode;
    protected override bool UseEntraMode => false;
}

[CollectionDefinition(Name)]
public sealed class DevelopmentPassThroughExplicitModeCollection
    : ICollectionFixture<DevelopmentPassThroughExplicitModeFixture>
{
    public const string Name = "ConformanceDevelopmentPassThroughExplicitMode";
}

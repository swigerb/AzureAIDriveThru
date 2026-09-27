using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Issue #76 part 2 (Rick's wave-plan comment on #20): "happy-hour flag honored (enabled or
/// not)" needs a FixedClock instant both inside and outside the window for the SAME running
/// backend that has BOTH fixture packs enabled (<see cref="TwoPersonaConformanceFixture"/>'s
/// test-alpha/test-beta), so a single pair of fixtures serves both personas' rows in
/// <c>PersonaHappyHourConformanceTests</c> rather than needing one fixture per persona. Reuses
/// <see cref="HappyHourBoundaryInstantsLoader"/>'s existing golden-sourced instants (case 1 =
/// 14:00:00, inside test-alpha's 14-16 America/Chicago window; case 0 = 13:59:59, just outside
/// it) instead of inventing new ones, keeping the golden file the single source of truth for
/// exactly which instants are on/off either side of the boundary.
///
/// PERSONAS/DEFAULT_PERSONA/PERSONAS_DIR and CONFORMANCE_FIXED_NOW are all read once at Python
/// module-import time, so (same reasoning as every other profile/persona override fixture in this
/// project) each instant needs its own dedicated collection/process.
/// </summary>
public sealed class TwoPersonaHappyHourWindowFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = HappyHourBoundaryInstantsLoader.At(1);

    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
    protected override IReadOnlyList<string>? Personas => [TwoPersonaConformanceFixture.PersonaA, TwoPersonaConformanceFixture.PersonaB];
    protected override string? Persona => TwoPersonaConformanceFixture.PersonaA;
    protected override string? PersonasDir => RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());
}

[CollectionDefinition(Name)]
public sealed class TwoPersonaHappyHourWindowCollection : ICollectionFixture<TwoPersonaHappyHourWindowFixture>
{
    public const string Name = "ConformanceTwoPersonaHappyHourWindow";
}

public sealed class TwoPersonaHappyHourOutsideWindowFixture : ConformanceFixture
{
    public static readonly DateTimeOffset Instant = HappyHourBoundaryInstantsLoader.At(0);

    protected override BackendProfile Profile => BackendProfiles.FixedClock(Instant);
    protected override IReadOnlyList<string>? Personas => [TwoPersonaConformanceFixture.PersonaA, TwoPersonaConformanceFixture.PersonaB];
    protected override string? Persona => TwoPersonaConformanceFixture.PersonaA;
    protected override string? PersonasDir => RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());
}

[CollectionDefinition(Name)]
public sealed class TwoPersonaHappyHourOutsideWindowCollection : ICollectionFixture<TwoPersonaHappyHourOutsideWindowFixture>
{
    public const string Name = "ConformanceTwoPersonaHappyHourOutsideWindow";
}

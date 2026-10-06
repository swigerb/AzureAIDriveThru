using System.Text.Json.Nodes;
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
    protected override string? PersonasDir => HappyHourPersonasDirOverride.Directory;
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
    protected override string? PersonasDir => HappyHourPersonasDirOverride.Directory;
}

[CollectionDefinition(Name)]
public sealed class TwoPersonaHappyHourOutsideWindowCollection : ICollectionFixture<TwoPersonaHappyHourOutsideWindowFixture>
{
    public const string Name = "ConformanceTwoPersonaHappyHourOutsideWindow";
}

/// <summary>
/// Refs #77: test-alpha's <c>soda_machine</c> is intentionally "down" by default in the shared
/// app/backend/tests/fixtures/personas pack -- <c>test_persona_binding.py</c>'s
/// <c>test_same_machine_key_is_down_for_alpha_but_operational_for_beta</c> (and this project's own
/// <c>test_a_persona_with_the_same_machine_key_operational_is_unaffected</c>) both depend on that
/// exact alpha=down/beta=operational split, so the shared fixture pack itself cannot change.
/// #77's new add-time <c>machine_unavailable</c> gate (correctly) now rejects adding "Alpha Cola"
/// on an unmodified copy of that pack, which collides with this file's pre-existing happy-hour
/// proof that also needs to add "Alpha Cola" (the one item Rick's PR #108 review required be
/// marked <c>happyHourDiscounted:true</c> for test-alpha) purely to exercise pricing, not machine
/// availability.
///
/// Mirrors the resolution <c>test_persona_binding.py</c>'s
/// <c>HappyHourBannerFromBoundPackTests</c> already uses for the identical Python-side collision:
/// bring alpha's soda_machine up for just this proof rather than touch the committed fixture pack
/// real search/gate tests still rely on. The Python side patches an in-memory persona object; a
/// conformance fixture launches a real backend subprocess against files on disk, so the
/// equivalent here is a one-time, on-disk, isolated copy of just test-alpha/test-beta with
/// alpha's soda_machine status flipped to "operational" -- used only by
/// <see cref="TwoPersonaHappyHourWindowFixture"/> and
/// <see cref="TwoPersonaHappyHourOutsideWindowFixture"/>, never by any other fixture pointed at
/// the shared <see cref="RepoPaths.FixturePersonasDirectory"/>.
/// </summary>
internal static class HappyHourPersonasDirOverride
{
    private static readonly Lazy<string> LazyDirectory = new(Build);

    public static string Directory => LazyDirectory.Value;

    private static string Build()
    {
        var sourceRoot = RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());
        var targetRoot = Path.Combine(
            Path.GetTempPath(), "conformance-happy-hour-personas-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(targetRoot);

        // persona_loader.py / PersonaCatalog.cs both load persona.schema.json and
        // menu.schema.json from PERSONAS_DIR itself (a sibling of the persona subfolders, not
        // inside any one persona's own folder) -- so the isolated copy needs these two schema
        // files alongside the copied packs, or backend startup fails validating nothing at all.
        File.Copy(
            Path.Combine(sourceRoot, "persona.schema.json"),
            Path.Combine(targetRoot, "persona.schema.json"), overwrite: true);
        File.Copy(
            Path.Combine(sourceRoot, "menu.schema.json"),
            Path.Combine(targetRoot, "menu.schema.json"), overwrite: true);

        CopyDirectory(
            Path.Combine(sourceRoot, TwoPersonaConformanceFixture.PersonaA),
            Path.Combine(targetRoot, TwoPersonaConformanceFixture.PersonaA));
        CopyDirectory(
            Path.Combine(sourceRoot, TwoPersonaConformanceFixture.PersonaB),
            Path.Combine(targetRoot, TwoPersonaConformanceFixture.PersonaB));

        SetSodaMachineOperational(
            Path.Combine(targetRoot, TwoPersonaConformanceFixture.PersonaA, "persona.json"));

        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(targetRoot);

        return targetRoot;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        System.IO.Directory.CreateDirectory(targetDir);
        foreach (var filePath in System.IO.Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, filePath);
            var destinationPath = Path.Combine(targetDir, relativePath);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(filePath, destinationPath, overwrite: true);
        }
    }

    private static void SetSodaMachineOperational(string personaJsonPath)
    {
        var json = JsonNode.Parse(File.ReadAllText(personaJsonPath))!.AsObject();
        json["machines"]!["soda_machine"]!["status"] = "operational";
        json["machines"]!["soda_machine"]!["label"] = "Our soda machine is up and running";
        File.WriteAllText(personaJsonPath, json.ToJsonString());
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only -- matches this project's other temp-directory test
            // helpers (e.g. ConformancePersonasTests), none of which guarantee deletion succeeds
            // on every platform/timing, and CI runners recycle their temp directories regardless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

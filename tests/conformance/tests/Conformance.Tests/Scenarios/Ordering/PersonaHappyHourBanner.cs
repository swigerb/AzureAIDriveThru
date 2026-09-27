using System.Text.Json;

namespace Conformance.Tests.Scenarios.Ordering;

/// <summary>
/// Rick's PR #108 review, required item 1: "an eligible drink's `update_order` result contains
/// Sonic's banner (READ from personas/sonic/persona.json, not typed in the test)". A harness-local
/// read of just the `pricing.happyHour.banner` field a persona's OWN `persona.json` declares --
/// deliberately NOT a dependency on app/backend/persona_loader.py's or a future C# backend's own
/// full schema validation, which belongs to the backend under test, not its test double (same
/// rationale as <see cref="Conformance.Fakes.MenuIndex.ResolveIndexPaths"/>). Used by both the
/// Sonic row (<c>HappyHourBoundaryTests.cs</c>) and the test-alpha row
/// (<c>PersonaHappyHourConformanceTests.cs</c>) so neither test types the expected banner text as
/// a literal that could silently drift from the pack it's meant to prove.
/// </summary>
internal static class PersonaHappyHourBanner
{
    public static string Read(string personasDir, string personaId)
    {
        var personaJsonPath = Path.Combine(personasDir, personaId, "persona.json");
        using var stream = File.OpenRead(personaJsonPath);
        using var document = JsonDocument.Parse(stream);
        var banner = document.RootElement.GetProperty("pricing").GetProperty("happyHour").GetProperty("banner").GetString();
        if (string.IsNullOrEmpty(banner))
        {
            throw new InvalidOperationException(
                $"persona.json for '{personaId}' under '{personasDir}' has no pricing.happyHour.banner.");
        }

        return banner;
    }
}

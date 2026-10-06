using System.Text.Json.Nodes;

namespace Backend.Tests.TestSupport;

/// <summary>
/// Copies the SAME non-brand-coupled fixture pack app/backend's own test suite uses
/// (app/backend/tests/fixtures/personas/{test-alpha,test-beta,test-delta,test-gamma}, see
/// DeltaFixture's own doc comment) into a throwaway temp directory, for schema-violation tests
/// that must not name a real persona pack in their own source. PersonaPackFixture mutates a copy
/// of the real, brand-named pack under personas/, which grows the checked-in rebrand-baseline
/// word-count ratchet (#76) every time a new test line names that brand; this fixture never
/// mentions a real brand, so a test using it never counts against that baseline.
/// </summary>
public sealed class NeutralPersonaPackFixture : IDisposable
{
    public string PersonasDir { get; }

    public NeutralPersonaPackFixture()
    {
        var sourceDir = Path.Combine(RepoRootLocator.Find(), "app", "backend", "tests", "fixtures", "personas");
        PersonasDir = Path.Combine(Path.GetTempPath(), "neutral-persona-tests-" + Guid.NewGuid().ToString("n"));
        CopyDirectory(sourceDir, PersonasDir);
    }

    /// <summary>Loads personaId/persona.json as a mutable JsonNode, applies <paramref name="mutate"/>,
    /// and writes it back.</summary>
    public void MutatePersonaJson(string personaId, Action<JsonObject> mutate)
    {
        var path = Path.Combine(PersonasDir, personaId, "persona.json");
        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException($"{path} did not parse as a JSON object.");
        mutate(node);
        File.WriteAllText(path, node.ToJsonString());
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(PersonasDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file left over from a test shouldn't fail teardown.
        }
    }
}

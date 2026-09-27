using System.Text.Json.Nodes;

namespace Backend.Tests.TestSupport;

/// <summary>
/// Test-side equivalent of app/backend/tests/test_persona_loader.py's `personas_copy` fixture:
/// copies the real personas/ tree (schemas + sonic pack) into a throwaway temp directory so tests
/// can mutate one field at a time without touching the real, shared personas/ tree. Disposing
/// deletes the temp directory.
/// </summary>
public sealed class PersonaPackFixture : IDisposable
{
    public string PersonasDir { get; }

    public PersonaPackFixture()
    {
        var sourceDir = Path.Combine(RepoRootLocator.Find(), "personas");
        PersonasDir = Path.Combine(Path.GetTempPath(), "beth-persona-tests-" + Guid.NewGuid().ToString("n"));
        CopyDirectory(sourceDir, PersonasDir);
    }

    /// <summary>Loads personas/&lt;personaId&gt;/persona.json as a mutable JsonNode, applies
    /// <paramref name="mutate"/>, and writes it back.</summary>
    public void MutatePersonaJson(string personaId, Action<JsonObject> mutate) =>
        MutateJsonFile(Path.Combine(PersonasDir, personaId, "persona.json"), mutate);

    public void MutateMenuJson(string personaId, Action<JsonObject> mutate) =>
        MutateJsonFile(Path.Combine(PersonasDir, personaId, "menu", "menuItems.json"), mutate);

    public void OverwritePersonaJson(string personaId, string rawContent) =>
        File.WriteAllText(Path.Combine(PersonasDir, personaId, "persona.json"), rawContent);

    public void DeletePromptsDir(string personaId) =>
        Directory.Delete(Path.Combine(PersonasDir, personaId, "prompts"), recursive: true);

    public void DeleteMenuFile(string personaId) =>
        File.Delete(Path.Combine(PersonasDir, personaId, "menu", "menuItems.json"));

    public void DeleteSchemaFile(string fileName) =>
        File.Delete(Path.Combine(PersonasDir, fileName));

    /// <summary>Copies an existing persona pack folder to a new id (also fixing up the copy's
    /// declared `id` field) so id/folder-matching and default-persona-fallback scenarios can be
    /// exercised with more than one enabled persona, without depending on any second real pack.</summary>
    public void DuplicatePersona(string sourceId, string newId)
    {
        CopyDirectory(Path.Combine(PersonasDir, sourceId), Path.Combine(PersonasDir, newId));
        MutatePersonaJson(newId, obj => obj["id"] = newId);
    }

    private static void MutateJsonFile(string path, Action<JsonObject> mutate)
    {
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

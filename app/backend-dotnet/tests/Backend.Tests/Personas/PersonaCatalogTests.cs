using Backend.Personas;
using Backend.Tests.TestSupport;
using System.Text.Json.Nodes;

namespace Backend.Tests.Personas;

/// <summary>
/// Fail-fast persona pack loading tests (issue #12's explicit validation requirement: "invalid
/// pack refuses to start"). Test names mirror app/backend/tests/test_persona_loader.py's
/// TestMutationSchemaViolations scenarios one for one, using PersonaPackFixture as the C#
/// equivalent of that suite's `personas_copy` fixture -- every scenario mutates a throwaway copy
/// of the real personas/sonic pack, not the shared tree.
/// </summary>
public sealed class PersonaCatalogTests
{
    [Fact]
    public void LoadsRealSonicPack_HappyPath()
    {
        var catalog = PersonaCatalog.Load();

        Assert.Contains("sonic", catalog.Ids);
        Assert.True(catalog.Contains("sonic"));
        Assert.Equal("sonic", catalog.DefaultPersonaId);
        Assert.Equal("sonic", catalog.Default.Id);
        Assert.NotNull(catalog.Get("sonic").Menu);
    }

    [Fact]
    public void UnknownPersonaId_ThrowsKeyNotFound()
    {
        var catalog = PersonaCatalog.Load();

        Assert.Throws<KeyNotFoundException>(() => catalog.Get("does-not-exist"));
        Assert.False(catalog.Contains("does-not-exist"));
    }

    [Fact]
    public void MissingPersonasDirectory_Throws()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "beth-missing-" + Guid.NewGuid().ToString("n"));

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: missingDir));
        Assert.Contains(missingDir, exc.Message);
    }

    [Fact]
    public void EmptyPersonasDirectory_ThrowsNoPersonasEnabled()
    {
        using var fixture = new PersonaPackFixture();
        Directory.Delete(Path.Combine(fixture.PersonasDir, "sonic"), recursive: true);

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("No personas enabled", exc.Message);
    }

    [Fact]
    public void MissingSchemaFile_Throws()
    {
        using var fixture = new PersonaPackFixture();
        fixture.DeleteSchemaFile("persona.schema.json");

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("persona.schema.json", exc.Message);
    }

    [Fact]
    public void MissingRequiredTopLevelField_Throws()
    {
        using var fixture = new PersonaPackFixture();
        fixture.MutatePersonaJson("sonic", obj => obj.Remove("displayName"));

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("sonic", exc.Message);
        Assert.Contains("displayName", exc.Message);
    }

    [Fact]
    public void WrongTypeForNestedField_Throws()
    {
        using var fixture = new PersonaPackFixture();
        fixture.MutatePersonaJson("sonic", obj => obj["locales"]!["supported"] = "not-an-array");

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("sonic", exc.Message);
    }

    [Fact]
    public void ExtraUnknownTopLevelField_Throws()
    {
        using var fixture = new PersonaPackFixture();
        fixture.MutatePersonaJson("sonic", obj => obj["unknownTopLevelField"] = "surprise");

        Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
    }

    [Fact]
    public void ExtraUnknownNestedField_ThemeAccents_Throws()
    {
        using var fixture = new PersonaPackFixture();
        fixture.MutatePersonaJson("sonic", obj =>
        {
            var accents = new JsonObject { ["unknownAccentField"] = "surprise" };
            obj["ui"]!["theme"]!["light"]!["accents"] = accents;
        });

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("sonic", exc.Message);
    }

    [Fact]
    public void IdFolderMismatch_Throws()
    {
        using var fixture = new PersonaPackFixture();
        fixture.MutatePersonaJson("sonic", obj => obj["id"] = "not-sonic");

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("not-sonic", exc.Message);
        Assert.Contains("sonic", exc.Message);
    }

    [Fact]
    public void MalformedPersonaJson_Throws()
    {
        using var fixture = new PersonaPackFixture();
        fixture.OverwritePersonaJson("sonic", "{ this is not valid json");

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("Malformed JSON", exc.Message);
    }

    [Fact]
    public void MissingMenuFile_Throws()
    {
        using var fixture = new PersonaPackFixture();
        fixture.DeleteMenuFile("sonic");

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("menu file not found", exc.Message);
    }

    [Fact]
    public void MenuSchemaViolation_Throws()
    {
        using var fixture = new PersonaPackFixture();

        // Any additionalProperties:false schema rejects an unknown field regardless of the pack's
        // other content, so this is an unambiguous, easy-to-target violation.
        fixture.MutateMenuJson("sonic", obj => obj["unknownMenuField"] = "surprise");

        Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
    }

    [Fact]
    public void MissingPromptsDirectory_Throws()
    {
        using var fixture = new PersonaPackFixture();
        fixture.DeletePromptsDir("sonic");

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("prompts directory not found", exc.Message);
    }

    [Fact]
    public void UnlistedEnabledPersona_Throws()
    {
        using var fixture = new PersonaPackFixture();

        var exc = Assert.Throws<PersonaValidationException>(
            () => PersonaCatalog.Load(personasDir: fixture.PersonasDir, personasEnv: "dunkin"));
        Assert.Contains("dunkin", exc.Message);
    }

    [Fact]
    public void DefaultPersonaNotEnabled_Throws()
    {
        using var fixture = new PersonaPackFixture();

        var exc = Assert.Throws<PersonaValidationException>(
            () => PersonaCatalog.Load(personasDir: fixture.PersonasDir, defaultPersonaEnv: "mcdonalds"));
        Assert.Contains("mcdonalds", exc.Message);
    }

    [Fact]
    public void DefaultPersona_FallsBackToSonic_WhenNoDefaultEnvSet()
    {
        using var fixture = new PersonaPackFixture();
        fixture.DuplicatePersona("sonic", "aaa-alphabetically-first");

        var catalog = PersonaCatalog.Load(personasDir: fixture.PersonasDir);

        Assert.Equal("sonic", catalog.DefaultPersonaId);
    }

    [Fact]
    public void DefaultPersona_FallsBackToFirstAlphabetical_WhenSonicNotEnabled()
    {
        using var fixture = new PersonaPackFixture();
        fixture.DuplicatePersona("sonic", "aaa-alphabetically-first");

        var catalog = PersonaCatalog.Load(
            personasDir: fixture.PersonasDir, personasEnv: "aaa-alphabetically-first");

        Assert.Equal("aaa-alphabetically-first", catalog.DefaultPersonaId);
    }
}

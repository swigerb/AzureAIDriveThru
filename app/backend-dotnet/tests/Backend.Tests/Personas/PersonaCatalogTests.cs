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
    public void ModelsLocalPipeline_Throws()
    {
        // Issue #155 dropped the `local` pipeline entirely: PersonaModels only declares `Realtime` and
        // `Cascade`, so a pack that still lists a `models.local` entry (a stale copy from before the
        // removal, or a hand-authored mistake) must be rejected the same way any other unknown field is.
        using var fixture = new PersonaPackFixture();
        fixture.MutatePersonaJson("sonic", obj =>
        {
            var local = new JsonObject
            {
                ["default"] = "phi-4-mini-local",
                ["allowed"] = new JsonArray("phi-4-mini-local"),
            };
            obj["models"]!["local"] = local;
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
    public void MenuItemKeyCollision_Throws()
    {
        // #128: two menu items that normalize to the same lookup key (e.g. two differently
        // parenthesized variants of the same base name) must fail startup, not silently let the
        // second one loaded win.
        using var fixture = new PersonaPackFixture();
        string firstItemName = "", collidingName = "";
        fixture.MutateMenuJson("sonic", obj =>
        {
            var items = obj["menuItems"]![0]!["items"]!.AsArray();
            var firstItem = items[0]!.AsObject();
            firstItemName = firstItem["name"]!.GetValue<string>();
            var colliding = firstItem.DeepClone().AsObject();
            collidingName = $"{firstItemName} (Party Size)";
            colliding["name"] = collidingName;
            items.Add(colliding);
        });

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("sonic", exc.Message);
        Assert.Contains(firstItemName, exc.Message);
        Assert.Contains(collidingName, exc.Message);
        Assert.Contains("same lookup key", exc.Message);
    }

    [Fact]
    public void MenuAliasCollisionWithAnotherItemsAlias_Throws()
    {
        // #128: an alias declared on two different items must fail startup -- an ambiguous alias
        // must never resolve silently to whichever item happened to load last.
        using var fixture = new PersonaPackFixture();
        string firstItemName = "", secondItemName = "";
        fixture.MutateMenuJson("sonic", obj =>
        {
            var items = obj["menuItems"]!.AsArray()
                .SelectMany(category => category!["items"]!.AsArray())
                .ToList();
            firstItemName = items[0]!["name"]!.GetValue<string>();
            secondItemName = items[1]!["name"]!.GetValue<string>();
            items[0]!.AsObject()["aliases"]!.AsArray().Add("duplicate test alias");
            items[1]!.AsObject()["aliases"]!.AsArray().Add("duplicate test alias");
        });

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("sonic", exc.Message);
        Assert.Contains("duplicate test alias", exc.Message);
        Assert.Contains(firstItemName, exc.Message);
        Assert.Contains(secondItemName, exc.Message);
    }

    [Fact]
    public void MenuAliasCollidingWithAnotherItemsOwnKey_Throws()
    {
        // #128: an alias that happens to normalize to a DIFFERENT item's own name (not just
        // another alias) must also fail startup -- this is the half of the rule that isn't a
        // plain alias-vs-alias duplicate.
        using var fixture = new PersonaPackFixture();
        string firstItemName = "", targetName = "";
        fixture.MutateMenuJson("sonic", obj =>
        {
            var items = obj["menuItems"]!.AsArray()
                .SelectMany(category => category!["items"]!.AsArray())
                .ToList();
            firstItemName = items[0]!["name"]!.GetValue<string>();
            targetName = items[1]!["name"]!.GetValue<string>();
            items[0]!.AsObject()["aliases"]!.AsArray().Add(targetName);
        });

        var exc = Assert.Throws<PersonaValidationException>(() => PersonaCatalog.Load(personasDir: fixture.PersonasDir));
        Assert.Contains("sonic", exc.Message);
        Assert.Contains(firstItemName, exc.Message);
        Assert.Contains(targetName, exc.Message);
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

    [Fact]
    public void RealPersonasDirectory_LoadsEveryPackFolderOnDisk_AndDefaultsToSonic()
    {
        // Rick's #114 review, required change 2: unlike PersonaPackFixture (which deliberately
        // copies only the sonic pack), this test loads the REAL personas/ directory directly, so
        // once #111 (Dunkin) and #112 (McDonald's) land, C# validates them too instead of the
        // catalog silently never seeing them.
        var personasDir = Path.Combine(RepoRootLocator.Find(), "personas");

        // Independent (catalog-free) ground truth: every immediate subfolder that contains a
        // persona.json, ordinal sorted -- the same disk-discovery convention PersonaCatalog.Load
        // itself documents (mirrors test_persona_loader.py's _discovered_persona_ids).
        var expectedIds = Directory.EnumerateDirectories(personasDir)
            .Select(d => Path.GetFileName(d)!)
            .Where(name => File.Exists(Path.Combine(personasDir, name, "persona.json")))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var catalog = PersonaCatalog.Load(personasDir: personasDir);

        Assert.Equal(expectedIds, catalog.Ids.OrderBy(id => id, StringComparer.Ordinal).ToList());
        Assert.Contains("sonic", catalog.Ids);
        Assert.Equal("sonic", catalog.DefaultPersonaId);
    }
}

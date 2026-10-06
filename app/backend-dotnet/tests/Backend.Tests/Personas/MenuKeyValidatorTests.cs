using System.Text.Json;
using Backend.Personas;

namespace Backend.Tests.Personas;

/// <summary>
/// Unit tests for <see cref="MenuKeyValidator"/> in isolation (mirrors app/backend/tests/
/// test_menu_utils.py's MenuKeyCollisionValidatorTests) -- pack-load-level fail-fast coverage
/// (an invalid pack refuses to start) lives in PersonaCatalogTests instead, alongside this
/// module's other mutation scenarios.
/// </summary>
public sealed class MenuKeyValidatorTests
{
    private static PersonaMenuItem MakeItem(string name, IEnumerable<string>? aliases = null) => new()
    {
        Name = name,
        Sizes = [],
        Description = "",
        Aliases = aliases?.ToList() ?? [],
    };

    private static PersonaMenu MakeMenu(params PersonaMenuItem[] items) => new()
    {
        MenuItems = [new PersonaMenuCategory { Category = "test-category", Items = [.. items] }],
    };

    [Theory]
    [InlineData("Tots (Extra Crispy)", "Tots")]
    [InlineData("Chili Cheese Tots (Extra Cheese)", "Chili Cheese Tots")]
    [InlineData("Cherry Limeade", "Cherry Limeade")]
    [InlineData("Tots (Extra Crispy) (No Salt)", "Tots")]
    [InlineData("Chili Cheese (Extra Cheese) Tots", "Chili Cheese Tots")]
    public void StripModifiers_MatchesPythonBehavior(string input, string expected) =>
        Assert.Equal(expected, MenuKeyValidator.StripModifiers(input));

    [Fact]
    public void StripModifiers_NestedParens_LeavesStrayCloseParen()
    {
        // Deliberate fail-safe, not a special case (see menu_utils.py's strip_modifiers doc
        // comment): a nested group only ever partially matches, so the result can never equal a
        // real menu key -- the item is classified as unknown rather than risking a wrong match.
        Assert.Equal("Tots Crispy)", MenuKeyValidator.StripModifiers("Tots (Extra (Really) Crispy)"));
    }

    [Theory]
    [InlineData("SuperSONIC\u00ae Double Cheeseburger", "supersonic double cheeseburger")]
    [InlineData("SONIC Smasher\u2122", "sonic smasher")]
    [InlineData("REESE\u2019S", "reese's")]
    public void MenuKey_NormalizesTrademarkAndApostropheSymbols(string input, string expected) =>
        Assert.Equal(expected, MenuKeyValidator.MenuKey(input));

    [Fact]
    public void ValidateNoCollisions_CleanMenu_DoesNotThrow()
    {
        var menu = MakeMenu(
            MakeItem("Tots", aliases: ["tater tots"]),
            MakeItem("Fries"));

        MenuKeyValidator.ValidateNoCollisions(menu, "test-pack", "");
    }

    [Fact]
    public void ValidateNoCollisions_TwoItemKeysCollide_Throws()
    {
        var menu = MakeMenu(MakeItem("Tots"), MakeItem("Tots (Extra Crispy)"));

        var exc = Assert.Throws<PersonaValidationException>(
            () => MenuKeyValidator.ValidateNoCollisions(menu, "test-pack", ""));
        Assert.Contains("Tots", exc.Message);
        Assert.Contains("Tots (Extra Crispy)", exc.Message);
        Assert.Contains("same lookup key", exc.Message);
        Assert.Contains("test-pack", exc.Message);
    }

    [Fact]
    public void ValidateNoCollisions_AliasCollidesWithAnotherItemsAlias_Throws()
    {
        var menu = MakeMenu(
            MakeItem("Tots", aliases: ["snack"]),
            MakeItem("Fries", aliases: ["snack"]));

        var exc = Assert.Throws<PersonaValidationException>(
            () => MenuKeyValidator.ValidateNoCollisions(menu, "test-pack", ""));
        Assert.Contains("snack", exc.Message);
        Assert.Contains("Tots", exc.Message);
        Assert.Contains("Fries", exc.Message);
    }

    [Fact]
    public void ValidateNoCollisions_AliasCollidesWithAnotherItemsOwnKey_Throws()
    {
        var menu = MakeMenu(
            MakeItem("Tots", aliases: ["Fries"]),
            MakeItem("Fries"));

        var exc = Assert.Throws<PersonaValidationException>(
            () => MenuKeyValidator.ValidateNoCollisions(menu, "test-pack", ""));
        Assert.Contains("Tots", exc.Message);
        Assert.Contains("Fries", exc.Message);
        Assert.Contains("own lookup key", exc.Message);
    }

    [Fact]
    public void ValidateNoCollisions_ForwardReferenceAliasVsLaterItemKey_IsStillCaught()
    {
        // The alias is declared on the FIRST item but only collides with the SECOND item's own
        // key, which is declared later in the file -- pass 1 must finish building every item's
        // key before pass 2 checks any alias, or this forward reference would be missed.
        var menu = MakeMenu(
            MakeItem("Tots", aliases: ["Fries"]),
            MakeItem("Fries"));

        Assert.Throws<PersonaValidationException>(
            () => MenuKeyValidator.ValidateNoCollisions(menu, "test-pack", ""));
    }

    [Fact]
    public void ValidateNoCollisions_AliasMatchingItsOwnItemsKey_IsNotACollision()
    {
        // An item's own alias normalizing to its own key (e.g. "Tots" aliased to "tots (large)")
        // must not raise -- only a DIFFERENT item's key/alias sharing the same normalized form is
        // a collision.
        var menu = MakeMenu(MakeItem("Tots", aliases: ["Tots", "tots (large)"]));

        MenuKeyValidator.ValidateNoCollisions(menu, "test-pack", "");
    }

    [Fact]
    public void ValidateNoCollisions_MenuPathIsFoldedIntoTheMessage()
    {
        var menu = MakeMenu(MakeItem("Tots"), MakeItem("Tots (Extra Crispy)"));

        var exc = Assert.Throws<PersonaValidationException>(
            () => MenuKeyValidator.ValidateNoCollisions(menu, "test-pack", "some/path/menuItems.json"));
        Assert.Contains("some/path/menuItems.json", exc.Message);
    }

    // ── shared golden vectors (Rick's #137 review note) ─────────────────────────────────────
    // app/backend/tests/fixtures/menu_key_vectors.json is the SAME file
    // app/backend/tests/test_menu_utils.py's own MenuKeyVectorTests loads -- one shared set of
    // ~20 input -> lookup-key examples asserted by BOTH backends' test suites, so a future change
    // to either _menu_key (Python) or MenuKeyValidator.MenuKey (C#) that quietly drifts the two
    // apart fails here in whichever backend didn't change, rather than only surfacing as a live
    // cross-backend behavior difference nobody wrote a test for.
    public static IEnumerable<object[]> SharedMenuKeyVectors()
    {
        var path = Path.Combine(
            RepoRootLocator.Find(), "app", "backend", "tests", "fixtures", "menu_key_vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var vector in doc.RootElement.EnumerateArray())
        {
            yield return [vector.GetProperty("input").GetString()!, vector.GetProperty("key").GetString()!];
        }
    }

    [Theory]
    [MemberData(nameof(SharedMenuKeyVectors))]
    public void MenuKey_MatchesTheSharedGoldenVectorFile(string input, string expectedKey) =>
        Assert.Equal(expectedKey, MenuKeyValidator.MenuKey(input));

    [Fact]
    public void SharedMenuKeyVectors_HasAtLeastTwentyEntries()
    {
        // Guard against the golden file itself quietly shrinking back below the ~20 examples
        // Rick's #137 review note asked for.
        Assert.True(SharedMenuKeyVectors().Count() >= 20,
            "Expected at least 20 shared menu-key vectors in menu_key_vectors.json.");
    }
}

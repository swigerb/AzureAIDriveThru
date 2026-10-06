using System.Text.RegularExpressions;

namespace Backend.Personas;

/// <summary>
/// Port of app/backend/menu_utils.py's <c>_menu_key</c>/<c>strip_modifiers</c>/
/// <c>validate_menu_key_collisions</c> (docs/dotnet_mapping.md, design doc section 6). Raises
/// <see cref="PersonaValidationException"/> if two menu items in a persona's own
/// <c>menu/menuItems.json</c> normalize to the same lookup key, or if an alias normalizes to
/// another item's own key or another item's own alias (issue #128).
///
/// <c>_menu_key</c>'s modifier-stripping is BY DESIGN -- "Tots" and "Tots (Extra Crispy)" must
/// classify identically -- but that exact same stripping silently collapsed a draft pack's five
/// differently-priced "(N piece)" nugget-size items into one key, with whichever one loaded last
/// winning and the other four unreachable/mispriced (the class of correctness bug #87 warns
/// about, first caught for issue #128). Raising here, at load time, means a colliding pack can
/// never load a degraded/last-write-wins catalog: the message always names the persona pack and
/// every colliding name, so the fix is obvious from the message alone -- the same fail-fast
/// convention as every other <see cref="PersonaValidationException"/> site in this module.
///
/// This is the ONE C# implementation of the rule (mirroring Python's ONE implementation in
/// <c>menu_utils.validate_menu_key_collisions</c>, reused by both <c>_load_menu_data</c> and
/// <c>persona_loader._load_one_persona</c>): <see cref="PersonaCatalog.LoadOnePersona"/> calls
/// this once, eagerly, for every enabled persona at process startup -- there is no lazy,
/// per-session <c>MenuCatalog</c>-equivalent in the C# backend yet for it to also guard, so a
/// single call site is correct here (unlike Python's "belt and suspenders" two call sites).
/// </summary>
internal static class MenuKeyValidator
{
    // Strips a parenthesized customization suffix, e.g. "Tots (Extra Crispy)" -> "Tots".
    // Mirrors menu_utils.py's _MODIFIER_SUFFIX_RE exactly (r"\s*\([^)]*\)\s*").
    private static readonly Regex ModifierSuffixRegex = new(@"\s*\([^)]*\)\s*", RegexOptions.Compiled);

    /// <summary>Mirrors menu_utils.py's <c>strip_modifiers</c>: strips every parenthesized
    /// customization group anywhere in the string (not just a trailing one), collapsing the
    /// result's whitespace runs (including Unicode whitespace such as NBSP, exactly like Python's
    /// <c>str.split()</c>).</summary>
    public static string StripModifiers(string? itemName)
    {
        var collapsed = ModifierSuffixRegex.Replace(itemName ?? "", " ");
        var parts = collapsed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    /// <summary>Mirrors menu_utils.py's <c>_menu_key</c>: lowercased, modifier-stripped,
    /// symbol-normalised lookup key. Do not change this without changing the Python
    /// implementation identically -- see design doc section 6, "one contract, two backends".</summary>
    public static string MenuKey(string? itemName) =>
        StripModifiers(itemName)
            .ToLowerInvariant()
            .Replace("\u00ae", "") // registered trademark symbol, e.g. "SONIC\u00ae Cheeseburger"
            .Replace("\u2122", "") // trademark symbol, e.g. "SONIC Smasher\u2122"
            .Replace("\u2019", "'"); // curly apostrophe -> plain ASCII apostrophe

    /// <summary>Throws <see cref="PersonaValidationException"/> if <paramref name="menu"/> has two
    /// menu items whose <see cref="MenuKey"/> collide, or an alias whose <see cref="MenuKey"/>
    /// collides with another item's own lookup key or another item's own alias (issue #128;
    /// design doc section 6). <paramref name="personaId"/> and <paramref name="menuPath"/> are
    /// folded into the exception message so a broken pack is fixable from the message alone.
    ///
    /// Two passes, not one: every item's own key must be fully known (pass 1) before any alias is
    /// checked (pass 2), so an alias declared on an EARLIER item that collides with a LATER
    /// item's own key is still caught, regardless of file order.</summary>
    public static void ValidateNoCollisions(PersonaMenu menu, string personaId, string menuPath)
    {
        var locationSuffix = string.IsNullOrEmpty(menuPath) ? "" : $" in {menuPath}";
        var itemNamesByKey = new Dictionary<string, string>();
        var orderedItems = new List<(string Name, List<string> Aliases)>();

        foreach (var category in menu.MenuItems)
        {
            foreach (var item in category.Items)
            {
                var name = item.Name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var key = MenuKey(name);
                if (itemNamesByKey.TryGetValue(key, out var existingName))
                {
                    throw new PersonaValidationException(
                        $"Persona '{personaId}': menu items '{existingName}' and '{name}' both " +
                        $"normalize to the same lookup key '{key}'{locationSuffix} -- rename one " +
                        "so they resolve distinctly (design doc section 6).");
                }

                itemNamesByKey[key] = name;
                orderedItems.Add((name, [.. item.Aliases]));
            }
        }

        var aliasOwner = new Dictionary<string, string>();
        foreach (var (name, aliases) in orderedItems)
        {
            foreach (var alias in aliases)
            {
                var aliasKey = MenuKey(alias);
                if (string.IsNullOrEmpty(aliasKey))
                {
                    continue;
                }

                if (itemNamesByKey.TryGetValue(aliasKey, out var ownerName) && ownerName != name)
                {
                    throw new PersonaValidationException(
                        $"Persona '{personaId}': alias '{alias}' on item '{name}' normalizes to " +
                        $"'{aliasKey}', which collides with menu item '{ownerName}''s own lookup " +
                        $"key{locationSuffix} -- an ambiguous alias must not resolve silently " +
                        "(design doc section 6).");
                }

                if (aliasOwner.TryGetValue(aliasKey, out var aliasOwnerName) && aliasOwnerName != name)
                {
                    throw new PersonaValidationException(
                        $"Persona '{personaId}': alias '{alias}' (normalized '{aliasKey}') is " +
                        $"declared on both '{aliasOwnerName}' and '{name}'{locationSuffix} -- an " +
                        "ambiguous alias must not resolve silently (design doc section 6).");
                }

                aliasOwner[aliasKey] = name;
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using Backend.Personas;

namespace Backend.Ordering;

/// <summary>
/// Builds this session's own bound persona's <see cref="MenuCatalog"/> (cached per persona id,
/// mirroring menu_utils.py's module-level <c>get_catalog_for_persona</c> cache -- a persona's menu
/// classification data never changes at runtime, so every session bound to the same persona shares
/// one immutable <see cref="MenuCatalog"/> instance) and a fresh, session-owned
/// <see cref="OrderState"/> from a <see cref="Persona"/>'s own <c>store</c>/<c>pricing</c> data
/// (#74/#113) -- never a hardcoded tax rate, happy-hour window, or banner string.
/// </summary>
internal static class PersonaOrderFactory
{
    private static readonly ConcurrentDictionary<string, MenuCatalog> MenuCatalogCache = new();

    /// <summary>This persona's own <see cref="MenuCatalog"/>, built once and cached by persona id.</summary>
    public static MenuCatalog GetMenuCatalog(Persona persona) =>
        MenuCatalogCache.GetOrAdd(persona.Id, _ => MenuCatalog.FromPersona(persona));

    /// <summary>A brand-new <see cref="OrderState"/> for one session bound to <paramref name="persona"/>
    /// -- never shared/cached across sessions (unlike <see cref="GetMenuCatalog"/>), since each
    /// session's order lines are its own.</summary>
    public static OrderState CreateOrderState(Persona persona)
    {
        var menu = GetMenuCatalog(persona);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(persona.Store.Timezone);
        var taxRate = decimal.Parse(persona.Pricing.TaxRate, CultureInfo.InvariantCulture);

        var happyHour = persona.Pricing.HappyHour;
        (int StartHour, int EndHour)? window = happyHour is not null
            ? (happyHour.StartHour, happyHour.EndHour)
            : null;
        var discount = happyHour is not null
            ? decimal.Parse(happyHour.PriceMultiplier, CultureInfo.InvariantCulture)
            : 1m;
        var announce = happyHour?.Announce ?? false;
        var banner = happyHour?.Banner ?? "";

        return new OrderState(menu, timeZone, taxRate, window, discount, announce, banner);
    }
}

namespace Backend.Configuration;

/// <summary>
/// Deployment-wide quantity-limit knobs (app/backend/tools.py's module-level
/// MAX_QUANTITY_PER_ITEM/MAX_TOTAL_ITEMS), read from config.yaml's OWN top-level
/// <c>business_rules</c> section -- NOT per-persona pricing data. Unlike tax rate/happy-hour
/// (#74/#113, moved onto each persona's own <c>persona.json</c> pricing block so a second brand
/// can price and schedule differently), quantity limits stayed a single deployment-wide dial in
/// Python (every persona sharing one drive-thru's throughput ceiling), so this C# port reads them
/// the same way: once, from the shared app/backend/config.yaml, never from a pack.
/// </summary>
public sealed class BusinessRulesConfig
{
    public int MaxItemQuantity { get; }
    public int MaxOrderItems { get; }

    private BusinessRulesConfig(int maxItemQuantity, int maxOrderItems)
    {
        MaxItemQuantity = maxItemQuantity;
        MaxOrderItems = maxOrderItems;
    }

    /// <summary>Mirrors tools.py's <c>_biz_cfg.get("max_item_quantity", 10)</c> /
    /// <c>_biz_cfg.get("max_order_items", 25)</c> -- same field names, same fallback defaults.</summary>
    public static BusinessRulesConfig FromAppConfig(AppConfig config)
    {
        var section = config.TryGetSection("business_rules");
        return new BusinessRulesConfig(
            GetInt(section, "max_item_quantity", 10),
            GetInt(section, "max_order_items", 25));
    }

    private static int GetInt(IDictionary<object, object>? section, string key, int fallback)
    {
        if (section is null || !section.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }
        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }
}

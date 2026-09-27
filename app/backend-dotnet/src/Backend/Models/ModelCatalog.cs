using Backend.Configuration;

namespace Backend.Models;

/// <summary>
/// Reads config.yaml's models.catalog section (design doc section 7's shared 3-layer model
/// config). This section does NOT exist in config.yaml yet -- defining it is Python's own #75,
/// still open -- so this catalog must tolerate the section being entirely absent (an empty
/// catalog is valid today) rather than fail startup. It deliberately does not add models.catalog
/// to config.yaml itself: that shared file's schema is #75's call, not this wave's, and both
/// backends must agree on it before either one writes to it.
///
/// Kept minimal (just the set of declared model ids) rather than guessing at a richer shape
/// (display name, pipeline, deployment mapping, ...) that #75 hasn't defined yet -- once #75
/// lands in Python, this catalog's shape should be revisited to match it exactly, per
/// docs/dotnet_mapping.md.
/// </summary>
public sealed class ModelCatalog
{
    private ModelCatalog(IReadOnlyCollection<string> ids) => Ids = ids;

    /// <summary>Model ids declared under config.yaml's models.catalog. Empty until #75 lands.</summary>
    public IReadOnlyCollection<string> Ids { get; }

    public bool Contains(string id) => Ids.Contains(id, StringComparer.Ordinal);

    public static ModelCatalog FromConfig(AppConfig config)
    {
        // Design doc section 7 says "models.catalog"; config.yaml today has no top-level "models"
        // section (only a singular "model" section, unrelated to a shared model catalog). Check
        // both the doc's literal top-level "models.catalog" and a "model.catalog" nested under
        // the existing "model" section, so this keeps working however #75 actually lands.
        var catalogMap = TryGetCatalog(config, "models") ?? TryGetCatalog(config, "model");
        if (catalogMap is null)
        {
            return new ModelCatalog(Array.Empty<string>());
        }

        var ids = catalogMap.Keys.Select(k => k.ToString()!).ToList();
        return new ModelCatalog(ids);
    }

    private static IDictionary<object, object>? TryGetCatalog(AppConfig config, string sectionName)
    {
        var section = config.TryGetSection(sectionName);
        if (section is not null &&
            section.TryGetValue("catalog", out var catalogRaw) &&
            catalogRaw is IDictionary<object, object> catalogMap)
        {
            return catalogMap;
        }
        return null;
    }
}

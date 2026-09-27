namespace Backend.Configuration;

/// <summary>
/// Port of app/backend/app.py's `_STATIC_IMMUTABLE_MAX_AGE`/`_STATIC_DEFAULT_MAX_AGE`, both read
/// from config.yaml's top-level `compression` section (not in <see
/// cref="AppConfig.RequiredTopLevelSections"/> -- an absent section, or an absent key within it,
/// is tolerated and defaults to the exact literal values Python falls back to, not an error).
/// Drives the persona asset/menu routes' Cache-Control header (Backend.Personas.PersonaRoutes).
/// </summary>
public sealed record AssetCacheConfig(long ImmutableMaxAgeSeconds, long DefaultMaxAgeSeconds)
{
    private const long DefaultImmutableMaxAgeSeconds = 31_536_000;
    private const long DefaultDefaultMaxAgeSeconds = 3600;

    public static AssetCacheConfig FromConfig(AppConfig config)
    {
        var section = config.TryGetSection("compression");
        return new AssetCacheConfig(
            ImmutableMaxAgeSeconds: ReadLong(section, "static_immutable_max_age", DefaultImmutableMaxAgeSeconds),
            DefaultMaxAgeSeconds: ReadLong(section, "static_default_max_age", DefaultDefaultMaxAgeSeconds));
    }

    private static long ReadLong(IDictionary<object, object>? section, string key, long fallback)
    {
        if (section is not null && section.TryGetValue(key, out var raw) && raw is not null)
        {
            return Convert.ToInt64(raw);
        }
        return fallback;
    }
}

namespace Backend.Configuration;

/// <summary>
/// Port of app/backend/tools.py's <c>_search_cfg</c>/<c>_cache_cfg</c> reads (config.yaml's
/// <c>search</c>/<c>cache</c> sections) -- deployment-wide dials for every persona's search calls
/// (there is no per-persona override of these three numbers; only the search endpoint/index/field
/// names vary per persona, via <c>persona.json</c>, not these). Same defaults as Python's own
/// <c>dict.get(key, default)</c> fallbacks so an empty/missing section behaves identically in both
/// backends.
/// </summary>
internal sealed class SearchConfig
{
    public int KNearestNeighbors { get; }
    public int TopResults { get; }
    public double TimeoutSeconds { get; }
    public double CacheTtlSeconds { get; }
    public int CacheMaxSize { get; }

    private SearchConfig(int kNearestNeighbors, int topResults, double timeoutSeconds, double cacheTtlSeconds, int cacheMaxSize)
    {
        KNearestNeighbors = kNearestNeighbors;
        TopResults = topResults;
        TimeoutSeconds = timeoutSeconds;
        CacheTtlSeconds = cacheTtlSeconds;
        CacheMaxSize = cacheMaxSize;
    }

    internal static SearchConfig FromAppConfig(AppConfig config)
    {
        var search = config.TryGetSection("search");
        var cache = config.TryGetSection("cache");
        return new SearchConfig(
            kNearestNeighbors: GetInt(search, "k_nearest_neighbors", 15),
            topResults: GetInt(search, "top_results", 3),
            timeoutSeconds: GetDouble(search, "timeout_seconds", 10),
            cacheTtlSeconds: GetDouble(cache, "search_ttl_seconds", 60.0),
            cacheMaxSize: GetInt(cache, "search_max_size", 128));
    }

    private static int GetInt(IDictionary<object, object>? section, string key, int fallback) =>
        section is not null && section.TryGetValue(key, out var raw) && int.TryParse(raw?.ToString(), out var value)
            ? value
            : fallback;

    private static double GetDouble(IDictionary<object, object>? section, string key, double fallback) =>
        section is not null && section.TryGetValue(key, out var raw) &&
        double.TryParse(raw?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

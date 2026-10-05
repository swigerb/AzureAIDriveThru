namespace SearchIndexIngestor;

/// <summary>
/// Resolves <c>AZURE_SEARCH_ENDPOINT</c> (setup_search_index.py's <c>run()</c>, line 471:
/// <c>os.environ["AZURE_SEARCH_ENDPOINT"]</c>) plus the <c>AZURE_SEARCH_SKIP_INDEX_SETUP</c> escape
/// hatch (<c>main()</c>, line 545: <c>os.environ.get("AZURE_SEARCH_SKIP_INDEX_SETUP") == "true"</c>).
///
/// Precedence for the endpoint matches <see cref="OpenAiSettingsResolver"/>'s own documented
/// precedence for the same reason (<c>load_azd_env()</c>'s <c>load_dotenv(path, override=True)</c>
/// runs before <c>run()</c> reads <c>os.environ</c>, so an azd-managed value always wins over a
/// pre-existing process environment variable): this port's own <c>--search-endpoint</c> CLI flag
/// (Python has none) &gt; azd default-environment value &gt; process environment variable &gt;
/// (none -- unlike the embedding deployment, Python has no default endpoint to fall back to).
/// </summary>
internal static class SearchEndpointResolver
{
    private const string SearchEndpointVariableName = "AZURE_SEARCH_ENDPOINT";
    public const string SkipIndexSetupVariableName = "AZURE_SEARCH_SKIP_INDEX_SETUP";

    /// <exception cref="InvalidOperationException">No <c>--search-endpoint</c> flag, no azd env
    /// value, and no <c>AZURE_SEARCH_ENDPOINT</c> environment variable. Python's own equivalent
    /// raises an unhandled <c>KeyError</c> with a stack trace in this situation; this port raises a
    /// clean, single-line, catchable message instead (see <see cref="PersonaTargeting"/>'s remarks
    /// on this port's error-handling convention).</exception>
    public static string Resolve(
        string? searchEndpointFlag,
        Func<string, string?> getEnvironmentVariable,
        IReadOnlyDictionary<string, string> azdEnvValues)
    {
        var endpoint = searchEndpointFlag;
        if (string.IsNullOrEmpty(endpoint))
        {
            endpoint = GetNonEmptyOrNull(azdEnvValues, SearchEndpointVariableName);
        }
        if (string.IsNullOrEmpty(endpoint))
        {
            endpoint = getEnvironmentVariable(SearchEndpointVariableName);
        }
        if (string.IsNullOrEmpty(endpoint))
        {
            throw new InvalidOperationException(
                "Azure AI Search endpoint not set -- pass --search-endpoint <url>, set it in the " +
                "azd default environment (azd env set AZURE_SEARCH_ENDPOINT <url>), or set the " +
                "AZURE_SEARCH_ENDPOINT environment variable.");
        }
        return endpoint;
    }

    /// <summary>
    /// setup_search_index.py's manual "never set automatically by infra" escape hatch (line
    /// 540-547): an azd-managed value still wins over a plain process environment variable (the
    /// same <c>load_dotenv(..., override=True)</c> precedence as the endpoint/every other azd-aware
    /// setting here), but there is no CLI flag for this one -- Python has none either.
    /// </summary>
    public static bool IsSkipIndexSetupEnabled(
        Func<string, string?> getEnvironmentVariable,
        IReadOnlyDictionary<string, string> azdEnvValues)
    {
        var value = GetNonEmptyOrNull(azdEnvValues, SkipIndexSetupVariableName) ??
            getEnvironmentVariable(SkipIndexSetupVariableName);
        return value == "true";
    }

    private static string? GetNonEmptyOrNull(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
}

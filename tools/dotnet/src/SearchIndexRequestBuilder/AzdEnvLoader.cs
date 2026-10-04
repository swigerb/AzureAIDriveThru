using System.Text;
using System.Text.Json;

namespace SearchIndexRequestBuilder;

/// <summary>
/// Reproduces setup_search_index.py's own <c>load_azd_env()</c> (lines 93-108) WITHOUT shelling
/// out to the real <c>azd</c> CLI: reads the same on-disk state <c>azd env list -o json</c>/
/// python-dotenv's <c>load_dotenv(path, override=True)</c> themselves read from, directly off
/// disk. This is the alternative Rick's PR #250 review offered ("via azd env list -o json or the
/// .azure/&lt;env&gt;/.env path the Python helper uses") -- chosen over shelling out to a real
/// <c>azd</c> process because it keeps this tool's tests free of any real <c>azd</c> invocation
/// (they build a temp <c>.azure</c> folder instead), matching every other Azure-free guarantee
/// this port already makes. The two files azd itself writes, and later reads from, are:
/// <list type="bullet">
/// <item><description><c>&lt;repoRoot&gt;/.azure/config.json</c> -- a
/// <c>{"defaultEnvironment": "&lt;name&gt;", ...}</c> object; this is exactly what
/// <c>azd env list -o json</c>'s own <c>IsDefault</c> flag is itself derived from.</description></item>
/// <item><description><c>&lt;repoRoot&gt;/.azure/&lt;name&gt;/.env</c> -- the azd-managed dotenv
/// file itself (Python's <c>DotEnvPath</c>), one <c>KEY="VALUE"</c> (always double-quoted, with
/// <c>\"</c>-escaped embedded quotes) assignment per line -- confirmed empirically via
/// <c>azd env new</c>/<c>azd env set</c> against a disposable local environment (never a real
/// deployment, never a network call).</description></item>
/// </list>
/// Unlike Python's <c>load_azd_env()</c>, which RAISES (<c>RuntimeError</c>) if azd or the default
/// env file isn't found, this port treats a missing/unreadable/malformed azd env as "contributes
/// nothing" and returns an empty dictionary -- a developer machine that has never run
/// <c>azd env new</c> is a normal, expected state for a REQUEST-BUILDING PREVIEW tool (unlike the
/// real ingestion script, this tool never required Azure access to begin with). Resolution falls
/// through to the process environment/default exactly as it did before this class existed; if
/// nothing resolves an endpoint, the caller still raises the one, existing clean error
/// (<see cref="OpenAiSettingsResolver"/>'s <see cref="InvalidOperationException"/>) -- never a
/// new/different error path for "azd missing" specifically.
/// </summary>
internal static class AzdEnvLoader
{
    private static readonly IReadOnlyDictionary<string, string> EmptyValues =
        new Dictionary<string, string>();

    /// <param name="repoRoot">The repo root to look for a <c>.azure/</c> folder under.</param>
    /// <param name="fileExists">Normally omitted -- defaults to <see cref="File.Exists(string)"/>.
    /// Lets tests substitute a fake filesystem instead of writing real temp files, though the
    /// project convention here (per PR #250 review) is a real temp <c>.azure</c> folder.</param>
    /// <param name="readAllText">Normally omitted -- defaults to
    /// <see cref="File.ReadAllText(string)"/>.</param>
    public static IReadOnlyDictionary<string, string> LoadDefaultEnvValues(
        string repoRoot,
        Func<string, bool>? fileExists = null,
        Func<string, string>? readAllText = null)
    {
        var exists = fileExists ?? File.Exists;
        var readText = readAllText ?? File.ReadAllText;

        try
        {
            var configPath = Path.Combine(repoRoot, ".azure", "config.json");
            if (!exists(configPath))
            {
                return EmptyValues;
            }

            using var configDocument = JsonDocument.Parse(readText(configPath));
            if (!configDocument.RootElement.TryGetProperty("defaultEnvironment", out var defaultEnvElement) ||
                defaultEnvElement.ValueKind != JsonValueKind.String)
            {
                return EmptyValues;
            }

            var defaultEnvironment = defaultEnvElement.GetString();
            if (string.IsNullOrEmpty(defaultEnvironment))
            {
                return EmptyValues;
            }

            var envFilePath = Path.Combine(repoRoot, ".azure", defaultEnvironment, ".env");
            if (!exists(envFilePath))
            {
                return EmptyValues;
            }

            return ParseDotEnvFile(readText(envFilePath));
        }
        catch
        {
            // Any unexpected failure here (malformed JSON, an I/O error, ...) is treated exactly
            // like "no azd env configured" -- never a crash, and never a special-cased error
            // message; see this class's own doc comment above.
            return EmptyValues;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseDotEnvFile(string contents)
    {
        var values = new Dictionary<string, string>();
        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = UnquoteDotEnvValue(line[(separatorIndex + 1)..]);
        }

        return values;
    }

    /// <summary>
    /// azd always double-quotes every value it writes, with a backslash-escaped <c>\"</c> for any
    /// embedded quote (confirmed empirically -- see this class's own doc comment). This parser is
    /// deliberately scoped to exactly that shape, not the full dotenv spec: it exists solely to
    /// read azd's own output, never a hand-edited .env file.
    /// </summary>
    private static string UnquoteDotEnvValue(string rawValue)
    {
        if (rawValue.Length < 2 || rawValue[0] != '"' || rawValue[^1] != '"')
        {
            return rawValue;
        }

        var inner = rawValue[1..^1];
        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                i++;
                builder.Append(inner[i] switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    'n' => '\n',
                    _ => inner[i],
                });
            }
            else
            {
                builder.Append(inner[i]);
            }
        }

        return builder.ToString();
    }
}

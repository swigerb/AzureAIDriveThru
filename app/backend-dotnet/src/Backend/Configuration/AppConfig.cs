using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Backend.Configuration;

/// <summary>
/// Port of app/backend/config_loader.py (docs/dotnet_mapping.md). Both backends load the SAME
/// app/backend/config.yaml -- it is not duplicated under app/backend-dotnet -- exactly like both
/// backends serving the same app/backend/static build and reading the same personas/ tree.
/// Fail-fast: throws on a missing file, non-mapping YAML, or a missing required top-level
/// section. Exposes the raw parsed sections rather than a fully strongly-typed model of every
/// field, since this wave only needs to prove the file loads and validates -- later waves (S3+)
/// can bind whichever specific sections they need (audio, business_rules, ...) without this class
/// having to know about every field up front.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Every config.yaml section config_loader.py's own _load() requires today.</summary>
    public static readonly IReadOnlyList<string> RequiredTopLevelSections =
        ["model", "business_rules", "cache", "audio", "connection"];

    private AppConfig(IReadOnlyDictionary<string, object?> root) => Root = root;

    /// <summary>Top-level YAML mapping, keyed by section name.</summary>
    public IReadOnlyDictionary<string, object?> Root { get; }

    public static AppConfig Load(string? path = null)
    {
        var configPath = path
            ?? Environment.GetEnvironmentVariable("CONFIG_PATH")
            ?? Path.Combine(RepoRootLocator.Find(), "app", "backend", "config.yaml");

        if (!File.Exists(configPath))
        {
            throw new ConfigValidationException($"Config file not found: {configPath}");
        }

        var yamlText = File.ReadAllText(configPath);
        object? raw;
        try
        {
            raw = new DeserializerBuilder().Build().Deserialize<object?>(yamlText);
        }
        catch (YamlException exc)
        {
            throw new ConfigValidationException($"Config file '{configPath}' is not valid YAML: {exc.Message}");
        }

        if (raw is not IDictionary<object, object> mapping)
        {
            throw new ConfigValidationException(
                $"Config file must be a YAML mapping, got {DescribeType(raw)}.");
        }

        var root = mapping.ToDictionary(kv => kv.Key.ToString()!, kv => (object?)kv.Value);

        var missing = RequiredTopLevelSections.Where(section => !root.ContainsKey(section)).ToList();
        if (missing.Count > 0)
        {
            throw new ConfigValidationException(
                $"Config file missing required sections: {string.Join(", ", missing)}");
        }

        return new AppConfig(root);
    }

    /// <summary>Gets a nested top-level section as a mapping, or null if absent/not a mapping.</summary>
    public IDictionary<object, object>? TryGetSection(string name) =>
        Root.TryGetValue(name, out var value) ? value as IDictionary<object, object> : null;

    private static string DescribeType(object? value) => value switch
    {
        null => "null",
        IList<object> => "a sequence",
        string => "a scalar string",
        _ => value.GetType().Name,
    };
}

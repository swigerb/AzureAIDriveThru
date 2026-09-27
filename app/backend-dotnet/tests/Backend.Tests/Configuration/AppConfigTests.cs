using Backend.Configuration;

namespace Backend.Tests.Configuration;

/// <summary>Fail-fast config.yaml loading tests, mirroring app/backend's config_loader.py
/// contract: missing file, malformed YAML, non-mapping YAML, and missing required top-level
/// sections all refuse to start.</summary>
public sealed class AppConfigTests
{
    [Fact]
    public void LoadsRealSharedConfig_HappyPath()
    {
        var config = AppConfig.Load();

        foreach (var section in AppConfig.RequiredTopLevelSections)
        {
            Assert.True(config.Root.ContainsKey(section), $"Expected section '{section}' to be present.");
        }
    }

    [Fact]
    public void MissingFile_Throws()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "beth-config-missing-" + Guid.NewGuid().ToString("n") + ".yaml");

        var exc = Assert.Throws<ConfigValidationException>(() => AppConfig.Load(missingPath));
        Assert.Contains(missingPath, exc.Message);
    }

    [Fact]
    public void MalformedYaml_Throws()
    {
        var path = WriteTempYaml("model: [unterminated");

        Assert.Throws<ConfigValidationException>(() => AppConfig.Load(path));
    }

    [Fact]
    public void NonMappingYaml_Throws()
    {
        var path = WriteTempYaml("- just\n- a\n- list\n");

        var exc = Assert.Throws<ConfigValidationException>(() => AppConfig.Load(path));
        Assert.Contains("must be a YAML mapping", exc.Message);
    }

    [Fact]
    public void MissingRequiredSection_Throws()
    {
        var path = WriteTempYaml("model:\n  foo: bar\n");

        var exc = Assert.Throws<ConfigValidationException>(() => AppConfig.Load(path));
        Assert.Contains("business_rules", exc.Message);
    }

    [Fact]
    public void TryGetSection_ReturnsNull_WhenAbsent()
    {
        var config = AppConfig.Load();

        Assert.Null(config.TryGetSection("this-section-does-not-exist"));
    }

    private static string WriteTempYaml(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "beth-config-" + Guid.NewGuid().ToString("n") + ".yaml");
        File.WriteAllText(path, content);
        return path;
    }
}

using Backend.Configuration;

namespace Backend.Tests.Configuration;

/// <summary>SecurityConfig.FromConfig tests -- the typed, tolerant view of config.yaml's
/// `security` section consumed by Realtime/RealtimeAuthGate.cs (PR #96 review, required item 1).
/// Exercises YamlDotNet's untyped-Deserialize quirk (every scalar comes back as a string, never a
/// real bool) the same way AppConfigTests exercises AppConfig's own fail-fast parsing: by writing
/// a real temp config.yaml and loading it through the real code path, rather than mocking.</summary>
public sealed class SecurityConfigTests
{
    [Fact]
    public void FromConfig_MissingSecuritySection_DefaultsMatchPython()
    {
        // rtmt.py: _security_cfg = _config.get("security", {}) -- an absent section is treated
        // exactly like an empty one, not an error.
        var config = LoadWithSecurity(section: null);

        var security = SecurityConfig.FromConfig(config);

        Assert.Empty(security.AllowedOrigins);
        Assert.False(security.RequireSessionToken);
    }

    [Fact]
    public void FromConfig_EmptySecuritySection_DefaultsMatchPython()
    {
        var config = LoadWithSecurity("security: {}\n");

        var security = SecurityConfig.FromConfig(config);

        Assert.Empty(security.AllowedOrigins);
        Assert.False(security.RequireSessionToken);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("False", false)]
    public void FromConfig_ParsesRequireSessionTokenBool_DespiteYamlDotNetStringScalars(string yamlValue, bool expected)
    {
        // YamlDotNet's untyped Deserialize<object?>() hands back every scalar as System.String
        // (docs/dotnet_mapping.md's "Gotchas" section, same issue PromptLoader.ParsePriority
        // works around) -- so `require_session_token: false` arrives as the *string* "False", not
        // the bool `false`. This must still parse correctly rather than falling through to the
        // tolerant-parser's `_ => false` default for every non-bool input.
        var config = LoadWithSecurity($"security:\n  require_session_token: {yamlValue}\n");

        var security = SecurityConfig.FromConfig(config);

        Assert.Equal(expected, security.RequireSessionToken);
    }

    [Fact]
    public void FromConfig_ParsesAllowedOriginsList()
    {
        var config = LoadWithSecurity(
            "security:\n  allowed_origins:\n    - https://example.com\n    - https://other.example.com\n");

        var security = SecurityConfig.FromConfig(config);

        Assert.Equal(["https://example.com", "https://other.example.com"], security.AllowedOrigins);
    }

    private static AppConfig LoadWithSecurity(string? section)
    {
        var yaml =
            "model:\n  foo: bar\n" +
            "business_rules:\n  foo: bar\n" +
            "cache:\n  foo: bar\n" +
            "audio:\n  foo: bar\n" +
            "connection:\n  foo: bar\n" +
            (section ?? string.Empty);

        var path = Path.Combine(Path.GetTempPath(), "squanchy-security-config-" + Guid.NewGuid().ToString("n") + ".yaml");
        File.WriteAllText(path, yaml);
        try
        {
            return AppConfig.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

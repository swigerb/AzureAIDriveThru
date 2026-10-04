using Backend.Auth;

namespace Backend.Tests.Auth;

/// <summary>
/// Byte-for-byte parity tests for EntraSettings.Resolve against app/backend/entra_auth.py's
/// resolve_settings state machine (ADR-002, issue #147). Each case below has a direct Python
/// counterpart; see entra_auth.py's resolve_settings for the authoritative branch order this
/// table mirrors.
/// </summary>
public sealed class EntraSettingsTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Client = "22222222-2222-2222-2222-222222222222";

    private static Func<string, string?> Env(params (string Key, string? Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void Resolve_UnsetAuthModeAndNoIds_ResolvesDevelopment()
    {
        var settings = EntraSettings.Resolve(Env(), isProduction: false);

        Assert.Equal(EntraMode.Development, settings.Mode);
        Assert.Null(settings.TenantId);
        Assert.Null(settings.ClientId);
        Assert.Equal("access_as_user", settings.ApiScope);
        Assert.Equal("DriveThru.User", settings.AppRole);
        Assert.Equal("https://login.microsoftonline.com/", settings.Instance);
    }

    [Fact]
    public void Resolve_UnsetAuthModeWithValidIds_InfersEntra()
    {
        var settings = EntraSettings.Resolve(
            Env(("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client)), isProduction: false);

        Assert.Equal(EntraMode.Entra, settings.Mode);
        Assert.Equal(Tenant, settings.TenantId);
        Assert.Equal(Client, settings.ClientId);
    }

    [Fact]
    public void Resolve_ExplicitEntra_WithValidIds_Succeeds()
    {
        var settings = EntraSettings.Resolve(
            Env(("AUTH_MODE", "Entra"), ("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client)),
            isProduction: false);

        Assert.Equal(EntraMode.Entra, settings.Mode);
    }

    [Fact]
    public void Resolve_ExplicitEntra_CaseInsensitive_Succeeds()
    {
        var settings = EntraSettings.Resolve(
            Env(("AUTH_MODE", "ENTRA"), ("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client)),
            isProduction: false);

        Assert.Equal(EntraMode.Entra, settings.Mode);
    }

    [Fact]
    public void Resolve_ExplicitEntra_NoIds_Throws()
    {
        var exc = Assert.Throws<EntraConfigException>(
            () => EntraSettings.Resolve(Env(("AUTH_MODE", "Entra")), isProduction: false));

        Assert.Contains("AUTH_MODE=Entra requires", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ExplicitEntra_PlaceholderTenant_Throws()
    {
        var exc = Assert.Throws<EntraConfigException>(() => EntraSettings.Resolve(
            Env(("AUTH_MODE", "Entra"), ("ENTRA_TENANT_ID", "your-tenant-id"), ("ENTRA_CLIENT_ID", Client)),
            isProduction: false));

        Assert.Contains("placeholder", exc.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_ExplicitEntra_MalformedGuid_Throws()
    {
        var exc = Assert.Throws<EntraConfigException>(() => EntraSettings.Resolve(
            Env(("AUTH_MODE", "Entra"), ("ENTRA_TENANT_ID", "not-a-guid"), ("ENTRA_CLIENT_ID", Client)),
            isProduction: false));

        Assert.Contains("not a valid GUID", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ExplicitDevelopment_WithIds_Throws()
    {
        var exc = Assert.Throws<EntraConfigException>(() => EntraSettings.Resolve(
            Env(("AUTH_MODE", "Development"), ("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client)),
            isProduction: false));

        Assert.Contains("must not be combined with Entra ids", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ExplicitDevelopment_InProduction_Throws()
    {
        var exc = Assert.Throws<EntraConfigException>(
            () => EntraSettings.Resolve(Env(("AUTH_MODE", "Development")), isProduction: true));

        Assert.Contains("refused in Production", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnsetAuthMode_InProduction_NoIds_Throws()
    {
        var exc = Assert.Throws<EntraConfigException>(
            () => EntraSettings.Resolve(Env(), isProduction: true));

        Assert.Contains("refused in Production", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnsetAuthMode_InProduction_WithIds_RequiresExplicitAuthMode()
    {
        var exc = Assert.Throws<EntraConfigException>(() => EntraSettings.Resolve(
            Env(("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client)), isProduction: true));

        Assert.Contains("AUTH_MODE=Entra to be set explicitly", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnsetAuthMode_InvalidIds_Throws()
    {
        var exc = Assert.Throws<EntraConfigException>(() => EntraSettings.Resolve(
            Env(("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", "bad")), isProduction: false));

        Assert.Contains("Entra authentication configuration is invalid", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnknownAuthMode_Throws()
    {
        var exc = Assert.Throws<EntraConfigException>(
            () => EntraSettings.Resolve(Env(("AUTH_MODE", "bogus")), isProduction: false));

        Assert.Contains("Unknown AUTH_MODE", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_EntraInProduction_NoAppSessionSecret_Throws()
    {
        var exc = Assert.Throws<EntraConfigException>(() => EntraSettings.Resolve(
            Env(("AUTH_MODE", "Entra"), ("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client)),
            isProduction: true));

        Assert.Contains("APP_SESSION_SECRET is required", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_EntraInProduction_WithAppSessionSecret_Succeeds()
    {
        var settings = EntraSettings.Resolve(
            Env(("AUTH_MODE", "Entra"), ("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client),
                ("APP_SESSION_SECRET", "a-sufficiently-long-production-secret")),
            isProduction: true);

        Assert.Equal(EntraMode.Entra, settings.Mode);
    }

    [Fact]
    public void Resolve_DefaultsApiScopeAndAppRole_WhenUnset()
    {
        var settings = EntraSettings.Resolve(
            Env(("AUTH_MODE", "Entra"), ("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client)),
            isProduction: false);

        Assert.Equal("access_as_user", settings.ApiScope);
        Assert.Equal("DriveThru.User", settings.AppRole);
    }

    [Fact]
    public void Resolve_HonoursExplicitApiScopeAndAppRole()
    {
        var settings = EntraSettings.Resolve(
            Env(("AUTH_MODE", "Entra"), ("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client),
                ("ENTRA_API_SCOPE", "custom.scope"), ("ENTRA_APP_ROLE", "Custom.Role")),
            isProduction: false);

        Assert.Equal("custom.scope", settings.ApiScope);
        Assert.Equal("Custom.Role", settings.AppRole);
    }

    [Fact]
    public void Resolve_DefaultsInstance_WhenUnset()
    {
        var settings = EntraSettings.Resolve(Env(), isProduction: false);

        Assert.Equal("https://login.microsoftonline.com/", settings.Instance);
    }

    [Fact]
    public void Resolve_NormalizesInstance_AddsTrailingSlash()
    {
        var settings = EntraSettings.Resolve(
            Env(("ENTRA_INSTANCE", "https://login.microsoftonline.com")), isProduction: false);

        Assert.Equal("https://login.microsoftonline.com/", settings.Instance);
    }

    [Fact]
    public void Resolve_AcceptsLoopbackHttpInstance_ForConformanceFakeIssuer()
    {
        // The conformance harness's FakeEntraIssuer serves plain HTTP on 127.0.0.1 (#143) --
        // this is the ONLY http:// shape _validate_instance accepts.
        var settings = EntraSettings.Resolve(
            Env(("ENTRA_INSTANCE", "http://127.0.0.1:5123/")), isProduction: false);

        Assert.Equal("http://127.0.0.1:5123/", settings.Instance);
    }

    [Fact]
    public void Resolve_RejectsNonLoopbackHttpInstance()
    {
        var exc = Assert.Throws<EntraConfigException>(
            () => EntraSettings.Resolve(Env(("ENTRA_INSTANCE", "http://evil.example.com/")), isProduction: false));

        Assert.Contains("must start with https://", exc.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Issuer_And_DiscoveryUrl_MatchPythonShape()
    {
        var settings = EntraSettings.Resolve(
            Env(("AUTH_MODE", "Entra"), ("ENTRA_TENANT_ID", Tenant), ("ENTRA_CLIENT_ID", Client)),
            isProduction: false);

        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/v2.0", settings.Issuer);
        Assert.Equal(
            $"https://login.microsoftonline.com/{Tenant}/v2.0/.well-known/openid-configuration",
            settings.DiscoveryUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("<script>")]
    [InlineData("has space")]
    [InlineData("your-tenant-id-here")]
    [InlineData("placeholder-value")]
    [InlineData("CHANGEME")]
    [InlineData("example-value")]
    [InlineData("TODO-fill-this-in")]
    [InlineData("xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx")]
    [InlineData("fixme")]
    public void ValidateEntraIds_RejectsPlaceholderTenant(string placeholder)
    {
        var error = EntraSettings.ValidateEntraIds(placeholder, Client);

        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateEntraIds_AcceptsValidIds()
    {
        var error = EntraSettings.ValidateEntraIds(Tenant, Client);

        Assert.Null(error);
    }

    [Fact]
    public void ValidateEntraIds_IsCaseInsensitiveOnGuidFormat()
    {
        var error = EntraSettings.ValidateEntraIds(Tenant.ToUpperInvariant(), Client.ToUpperInvariant());

        Assert.Null(error);
    }
}

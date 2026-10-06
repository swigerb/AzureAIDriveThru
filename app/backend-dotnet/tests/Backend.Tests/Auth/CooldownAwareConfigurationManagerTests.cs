using System.Net.Http;
using System.Text.Json;
using Backend.Auth;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Tests.Auth;

/// <summary>
/// Unit tests for CooldownAwareConfigurationManager (issue #147/#226, Rick's HIGH finding #1 and
/// LOW-MED finding #3) -- isolates the decorator's own GetBaseConfigurationAsync/HasConfiguration/
/// RequestRefresh logic from the real network and from JwtBearerHandler's pipeline plumbing (that
/// end-to-end proof lives in EntraPipelineCooldownTests). A controllable fake subclass of the real
/// Microsoft.IdentityModel.Protocols.ConfigurationManager&lt;T&gt; stands in for "_inner" so each
/// test can force a specific fetch outcome deterministically.
/// </summary>
public sealed class CooldownAwareConfigurationManagerTests
{
    /// <summary>
    /// A real <see cref="ConfigurationManager{T}"/> (needed because
    /// <see cref="CooldownAwareConfigurationManager"/>'s constructor requires the concrete type,
    /// exactly mirroring how EntraAuthentication.ConfigureJwtBearer constructs it in production)
    /// whose GetBaseConfigurationAsync is overridden to deterministically succeed or fail without
    /// any network call -- IdentityModel.Protocols does not seal this class or its method.
    /// </summary>
    private sealed class FakeInnerConfigurationManager()
        : ConfigurationManager<OpenIdConnectConfiguration>("http://unused/", new AlwaysSucceedsRetriever())
    {
        public Exception? NextFailure { get; set; }

        public override Task<BaseConfiguration> GetBaseConfigurationAsync(CancellationToken cancel) =>
            NextFailure is { } failure ? Task.FromException<BaseConfiguration>(failure) : base.GetBaseConfigurationAsync(cancel);
    }

    private sealed class AlwaysSucceedsRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel) =>
            Task.FromResult(new OpenIdConnectConfiguration());
    }

    private static FakeInnerConfigurationManager Inner() => new();

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CooldownAwareConfigurationManager(null!, new DiscoveryFailureGate()));
    }

    [Fact]
    public void Constructor_NullGate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CooldownAwareConfigurationManager(Inner(), null!));
    }

    [Fact]
    public async Task GetBaseConfigurationAsync_Success_SetsHasConfiguration_AndDoesNotArmGate()
    {
        var gate = new DiscoveryFailureGate();
        var decorator = new CooldownAwareConfigurationManager(Inner(), gate);

        var configuration = await decorator.GetBaseConfigurationAsync(CancellationToken.None);

        Assert.NotNull(configuration);
        Assert.True(decorator.HasConfiguration);
        Assert.False(gate.IsInCooldown());
    }

    [Fact]
    public async Task GetBaseConfigurationAsync_ColdFailure_ArmsGate_AndRethrows()
    {
        // Fix #1 (HIGH): the one case this whole decorator exists for -- a genuine fetch failure
        // with nothing held yet must arm the 30s cooldown, synchronously, on the awaited call.
        var gate = new DiscoveryFailureGate();
        var inner = Inner();
        inner.NextFailure = new HttpRequestException("connection reset");
        var decorator = new CooldownAwareConfigurationManager(inner, gate);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => decorator.GetBaseConfigurationAsync(CancellationToken.None));

        Assert.Equal("connection reset", thrown.Message);
        Assert.False(decorator.HasConfiguration);
        Assert.True(gate.IsInCooldown());
    }

    [Theory]
    [MemberData(nameof(DiscoveryFailureExceptions))]
    public async Task GetBaseConfigurationAsync_ColdFailure_RecognizesAllDiscoveryFailureShapes(Exception failure)
    {
        var gate = new DiscoveryFailureGate();
        var inner = Inner();
        inner.NextFailure = failure;
        var decorator = new CooldownAwareConfigurationManager(inner, gate);

        await Assert.ThrowsAnyAsync<Exception>(() => decorator.GetBaseConfigurationAsync(CancellationToken.None));

        Assert.True(gate.IsInCooldown());
    }

    public static TheoryData<Exception> DiscoveryFailureExceptions => new()
    {
        new HttpRequestException("reset"),
        new IOException("incomplete read"),
        new JsonException("malformed body"),
        new OperationCanceledException("timed out"),
        // IdentityModel.Protocols.ConfigurationManager<T> wraps the real cause in its own
        // InvalidOperationException ("IDX20803") when cold -- must be recognized via InnerException.
        new InvalidOperationException("IDX20803", new HttpRequestException("reset")),
    };

    [Fact]
    public async Task GetBaseConfigurationAsync_ColdFailure_UnrecognizedExceptionType_DoesNotArmGate()
    {
        // Only genuine network/backchannel/parse failures (or exceptions wrapping one) should ever
        // arm the cooldown -- an unrelated exception type must propagate untouched.
        var gate = new DiscoveryFailureGate();
        var inner = Inner();
        inner.NextFailure = new InvalidOperationException("unrelated, no network cause");
        var decorator = new CooldownAwareConfigurationManager(inner, gate);

        await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.GetBaseConfigurationAsync(CancellationToken.None));

        Assert.False(gate.IsInCooldown());
    }

    [Fact]
    public async Task GetBaseConfigurationAsync_WarmFailure_DoesNotArmGate_ButStillRethrows()
    {
        // A background-refresh-style failure AFTER a configuration is already held must not arm
        // the cooldown -- that would re-block already-resolvable, cached-kid tokens (Rick's HIGH
        // finding's own failure mode, applied to the decorator in isolation).
        var gate = new DiscoveryFailureGate();
        var inner = Inner();
        var decorator = new CooldownAwareConfigurationManager(inner, gate);
        await decorator.GetBaseConfigurationAsync(CancellationToken.None);
        Assert.True(decorator.HasConfiguration);

        inner.NextFailure = new HttpRequestException("transient refetch failure while warm");
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => decorator.GetBaseConfigurationAsync(CancellationToken.None));

        Assert.Equal("transient refetch failure while warm", thrown.Message);
        Assert.True(decorator.HasConfiguration);
        Assert.False(gate.IsInCooldown());
    }

    [Fact]
    public async Task GetConfigurationAsync_InterfaceEntrypoint_RoutesThroughSameLogic()
    {
        // Not reachable on the real JwtBearerHandler pipeline, but must still behave correctly
        // (including arming the gate) for any caller -- test or otherwise -- that invokes it.
        var gate = new DiscoveryFailureGate();
        var inner = Inner();
        inner.NextFailure = new IOException("reset");
        IConfigurationManager<OpenIdConnectConfiguration> decorator = new CooldownAwareConfigurationManager(inner, gate);

        await Assert.ThrowsAsync<IOException>(() => decorator.GetConfigurationAsync(CancellationToken.None));

        Assert.True(gate.IsInCooldown());
    }

    [Fact]
    public void RequestRefresh_DelegatesToInner()
    {
        // RequestRefresh can't be observed via a public property, so this exercises it purely for
        // "does not throw, and the inner's own call completes" -- IdentityModel.Protocols's own
        // RequestRefresh is itself throttled/single-flighted internally; nothing to assert further
        // without reflecting into private inner state, which would couple this test to IdentityModel
        // internals rather than our own decorator's contract.
        var decorator = new CooldownAwareConfigurationManager(Inner(), new DiscoveryFailureGate());

        var exception = Record.Exception(decorator.RequestRefresh);

        Assert.Null(exception);
    }

    [Fact]
    public void ConfigureJwtBearer_DefaultsLastKnownGoodLifetimeTo300Seconds()
    {
        // #226 Rick's review, fix #3 (LOW-MED): pins the production default directly -- the
        // EntraPipelineCooldownTests.KeyRotation_* pipeline test proves the MECHANISM actually
        // bounds a rotated-out key's grace period, but it exercises that mechanism with a
        // test-shrunk lastKnownGoodLifetime override (to avoid a real 300-second wait), so it
        // can't by itself catch a regression that silently widens/removes the production 300s
        // default (e.g. reverting to IdentityModel's own 1-hour BaseConfigurationManager default).
        // This cheap, non-timing-based property check closes that gap.
        var settings = new EntraSettings(EntraMode.Entra, "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", "access_as_user", "DriveThru.User",
            "https://login.microsoftonline.com/");
        var options = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions();

        EntraAuthentication.ConfigureJwtBearer(options, settings);

        var manager = Assert.IsType<CooldownAwareConfigurationManager>(options.ConfigurationManager);
        Assert.Equal(TimeSpan.FromSeconds(300), manager.LastKnownGoodLifetime);
    }

    [Fact]
    public void ConfigureJwtBearer_LastKnownGoodLifetimeOverride_IsApplied()
    {
        var settings = new EntraSettings(EntraMode.Entra, "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", "access_as_user", "DriveThru.User",
            "https://login.microsoftonline.com/");
        var options = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions();
        var overrideLifetime = TimeSpan.FromMilliseconds(250);

        EntraAuthentication.ConfigureJwtBearer(options, settings, lastKnownGoodLifetime: overrideLifetime);

        var manager = Assert.IsType<CooldownAwareConfigurationManager>(options.ConfigurationManager);
        Assert.Equal(overrideLifetime, manager.LastKnownGoodLifetime);
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Backend.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Tests.Auth;

/// <summary>
/// #226 Rick's HIGH finding (fix #1): pipeline-level integration tests against a REAL JwtBearerHandler
/// host and a real (loopback-socket) fake IdP -- the existing unit tests all call OnAuthenticationFailed
/// directly with a fake exception, which can never catch the actual production bug (JwtBearerHandler
/// itself never routes a metadata-fetch failure through that event; see
/// CooldownAwareConfigurationManager's doc comment). These tests boot the app's own
/// EntraAuthentication.ConfigureJwtBearer/BuildAccessPolicy wiring on an ASP.NET Core TestServer
/// (inbound requests, no real socket) pointed at a genuinely Kestrel-hosted fake discovery/JWKS
/// endpoint (outbound calls over a real loopback socket, since JwtBearerOptions.Backchannel is a
/// real HttpClient) -- proving the fix end-to-end rather than unit-testing the decorator in isolation
/// (that's CooldownAwareConfigurationManagerTests's job).
/// </summary>
public sealed class EntraPipelineCooldownTests
{
    private enum ServerMode
    {
        Succeed,
        Fail,
        Hang,
    }

    /// <summary>A genuinely Kestrel-hosted (real loopback socket) fake of the two Entra endpoints
    /// JwtBearerHandler's ConfigurationManager fetches -- discovery document and JWKS -- with a
    /// mutable <see cref="Mode"/> and request counters so a test can simulate a cold outage, a
    /// recovery, or a warm-cache-with-background-fault, and assert exactly how many real HTTP
    /// requests the IdP actually received.</summary>
    private sealed class FakeDiscoveryServer : IAsyncDisposable
    {
        public const string Kid = "pipeline-test-key";

        private RSA _rsa;
        private string _kid = Kid;
        private readonly List<RSA> _allKeys = [];
        private WebApplication? _app;
        private int _discoveryRequestCount;
        private int _jwksRequestCount;

        public FakeDiscoveryServer()
        {
            _rsa = RSA.Create(2048);
            _allKeys.Add(_rsa);
        }

        public ServerMode Mode { get; set; } = ServerMode.Succeed;

        public int DiscoveryRequestCount => _discoveryRequestCount;

        public int JwksRequestCount => _jwksRequestCount;

        public int TotalRequestCount => _discoveryRequestCount + _jwksRequestCount;

        public string CurrentKid => _kid;

        public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0/");

        public string Tenant { get; } = "11111111-1111-1111-1111-111111111111";

        public string Issuer => new Uri(BaseUri, $"{Tenant}/v2.0").ToString();

        public string DiscoveryUrl => new Uri(BaseUri, $"{Tenant}/v2.0/.well-known/openid-configuration").ToString();

        private string JwksUri => new Uri(BaseUri, $"{Tenant}/discovery/v2.0/keys").ToString();

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();

            app.MapGet("/{tenant}/v2.0/.well-known/openid-configuration", async (HttpContext ctx) =>
            {
                Interlocked.Increment(ref _discoveryRequestCount);
                await RespondAsync(ctx, () => $$"""
                    {"issuer": "{{Issuer}}", "jwks_uri": "{{JwksUri}}",
                     "id_token_signing_alg_values_supported": ["RS256"]}
                    """);
            });

            app.MapGet("/{tenant}/discovery/v2.0/keys", async (HttpContext ctx) =>
            {
                Interlocked.Increment(ref _jwksRequestCount);
                await RespondAsync(ctx, BuildJwksJson);
            });

            await app.StartAsync();
            _app = app;
            BaseUri = new Uri(app.Urls.First());
        }

        private async Task RespondAsync(HttpContext ctx, Func<string> bodyFactory)
        {
            switch (Mode)
            {
                case ServerMode.Fail:
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    break;
                case ServerMode.Hang:
                    // Only ends when the client gives up (BackchannelTimeout) and disconnects, or
                    // the test host shuts down -- never resolves on its own, matching a genuinely
                    // wedged/unreachable IdP rather than merely a slow one.
                    await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                    break;
                default:
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(bodyFactory());
                    break;
            }
        }

        private string BuildJwksJson()
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = _kid });
            return $$"""
                {"keys": [{"kty": "{{jwk.Kty}}", "use": "sig", "kid": "{{_kid}}", "n": "{{jwk.N}}", "e": "{{jwk.E}}", "alg": "RS256"}]}
                """;
        }

        /// <summary>
        /// #226 Rick's review, fix #3 (LOW-MED) key-rotation pipeline test: simulates the IdP
        /// retiring the currently-published signing key and starting to publish a brand new one
        /// instead -- from this call onward, <see cref="BuildJwksJson"/> (and the default signing
        /// key used by future <see cref="MintToken"/> calls) serves ONLY the new key/kid; the OLD
        /// (RSA key, kid) pair is returned so the caller can keep minting "already-rotated-out"
        /// tokens with it via <see cref="MintToken"/>'s <c>signingKeyOverride</c>/<c>kidOverride</c>
        /// parameters, exactly mirroring how a real client holding a stale cached token behaves.
        /// </summary>
        public (RSA OldKey, string OldKid) RotateKey()
        {
            var oldKey = _rsa;
            var oldKid = _kid;
            _rsa = RSA.Create(2048);
            _kid = $"pipeline-test-key-rotated-{Guid.NewGuid():N}";
            _allKeys.Add(_rsa);
            return (oldKey, oldKid);
        }

        public string MintToken(EntraSettings settings, string? kidOverride = null, RSA? signingKeyOverride = null)
        {
            var claims = new List<Claim>
            {
                new("tid", Tenant),
                new("oid", "33333333-3333-3333-3333-333333333333"),
            };
            claims.Add(new Claim("roles", settings.AppRole));
            claims.Add(new Claim("scp", settings.ApiScope));
            var now = DateTime.UtcNow;
            var signingKey = new RsaSecurityKey(signingKeyOverride ?? _rsa) { KeyId = kidOverride ?? _kid };
            var token = new JwtSecurityToken(
                issuer: Issuer,
                audience: settings.ClientId,
                claims: claims,
                notBefore: now.AddMinutes(-5),
                expires: now.AddMinutes(15),
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
            token.Payload["roles"] = new[] { settings.AppRole };
            if (kidOverride is not null)
            {
                token.Header["kid"] = kidOverride;
            }

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }

            foreach (var key in _allKeys)
            {
                key.Dispose();
            }
        }
    }

    private static EntraSettings SettingsFor(FakeDiscoveryServer server) => new(
        EntraMode.Entra,
        server.Tenant,
        "22222222-2222-2222-2222-222222222222",
        "access_as_user",
        "DriveThru.User",
        server.BaseUri.ToString());

    /// <summary>Boots the app's own ConfigureJwtBearer/BuildAccessPolicy wiring on a TestServer
    /// (inbound: no real socket) pointed at <paramref name="server"/>'s real loopback DiscoveryUrl
    /// (outbound: a genuine HttpClient over a real socket) -- a 500ms BackchannelTimeout keeps
    /// Hang-mode scenarios fast instead of waiting the production 10s default. The returned
    /// <see cref="BaseConfigurationManager"/> is the exact <c>options.ConfigurationManager</c>
    /// instance (fix #1's <see cref="CooldownAwareConfigurationManager"/>) the real pipeline uses
    /// -- the #226 LOW-MED-finding key-rotation test calls <c>RequestRefresh()</c> on it directly
    /// to force an immediate, un-throttled re-fetch (IdentityModel's own first-ever-call bypass,
    /// see <c>ConfigurationManager&lt;T&gt;.RequestRefreshBackgroundThread</c>) instead of waiting
    /// out the real 12-hour AutomaticRefreshInterval or the 5-minute-minimum RefreshInterval.</summary>
    private static async Task<(IHost Host, HttpClient Client, DiscoveryFailureGate Gate, BaseConfigurationManager ConfigurationManager)> StartAppAsync(
        FakeDiscoveryServer server, FakeTimeProvider? timeProvider = null, TimeSpan? lastKnownGoodLifetime = null, CancellationToken cancellationToken = default)
    {
        var settings = SettingsFor(server);
        var gate = new DiscoveryFailureGate(timeProvider);
        BaseConfigurationManager? capturedConfigurationManager = null;
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                        .AddJwtBearer(options =>
                        {
                            EntraAuthentication.ConfigureJwtBearer(
                                options, settings, gate, backchannelTimeout: TimeSpan.FromMilliseconds(500),
                                lastKnownGoodLifetime: lastKnownGoodLifetime);
                            capturedConfigurationManager = (BaseConfigurationManager)options.ConfigurationManager!;
                        });
                    services.AddSingleton<IAuthorizationHandler, EntraAccessRequirementHandler>();
                    services.AddAuthorization(options =>
                    {
                        var policy = EntraAuthentication.BuildAccessPolicy(settings);
                        options.DefaultPolicy = policy;
                        options.FallbackPolicy = policy;
                    });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapGet("/protected", () => Results.Ok()));
                });
            });
        var host = await hostBuilder.StartAsync(cancellationToken);
        // Force IOptionsMonitor to resolve JwtBearerOptions (and so run the AddJwtBearer configure
        // delegate above, populating capturedConfigurationManager) before returning -- ASP.NET
        // Core's options system only invokes it lazily, on first Get()/CurrentValue access, which
        // in production doesn't happen until the first real request arrives.
        _ = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        return (host, host.GetTestClient(), gate, capturedConfigurationManager!);
    }

    private static HttpRequestMessage ProtectedRequest(string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Fact]
    public async Task ColdOutage_AtMostOneFetchPerWindow_AndFastFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider();
        await using var server = new FakeDiscoveryServer();
        await server.StartAsync();
        server.Mode = ServerMode.Hang;
        var (host, client, gate, _) = await StartAppAsync(server, timeProvider, cancellationToken: ct);
        using var disposableHost = host;

        // First request: the only one allowed to actually touch the (hung) network -- it eats the
        // 500ms BackchannelTimeout, then fails closed (401), arming the 30s cooldown. Must be a
        // well-formed (if garbage-signed, since the IdP never answers to prove otherwise) JWT --
        // JsonWebTokenHandler rejects an unparseable bearer string before ever needing a signing
        // key, which would never reach (and so never exercise) the ConfigurationManager fetch this
        // test is about.
        var settingsForTokenShape = SettingsFor(server);
        var wellFormedToken = server.MintToken(settingsForTokenShape);
        var first = await client.SendAsync(ProtectedRequest(wellFormedToken), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(1, server.DiscoveryRequestCount);
        Assert.True(gate.IsInCooldown());

        // Several more requests within the window: short-circuited by OnMessageReceived before
        // ConfigurationManager is ever touched again -- fast (no BackchannelTimeout wait) and the
        // IdP sees no additional traffic. This is Rick's exact complaint: "concurrent requests
        // queue 10/20/30/40s" must no longer happen.
        for (var i = 0; i < 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.SendAsync(ProtectedRequest(wellFormedToken), ct);
            sw.Stop();
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(
                sw.ElapsedMilliseconds < 400,
                $"iter {i}: expected a fast short-circuited 401, took {sw.ElapsedMilliseconds}ms, discovery={server.DiscoveryRequestCount} jwks={server.JwksRequestCount} cooldown={gate.IsInCooldown()}");
        }

        Assert.Equal(1, server.DiscoveryRequestCount);

        // Recovery: once the 30s window elapses, the IdP (now healthy) gets exactly one more
        // fetch attempt, and a request bearing a valid, cached-kid token succeeds.
        timeProvider.Advance(TimeSpan.FromSeconds(31));
        server.Mode = ServerMode.Succeed;
        var settings = SettingsFor(server);
        var validToken = server.MintToken(settings);
        var afterRecovery = await client.SendAsync(ProtectedRequest(validToken), ct);
        Assert.Equal(HttpStatusCode.OK, afterRecovery.StatusCode);
        Assert.Equal(2, server.DiscoveryRequestCount);
    }

    [Fact]
    public async Task WarmCache_IdpFaultAndForgedKidFlood_CachedKidTokenStillSucceeds_AtMostOneExtraFetch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new FakeDiscoveryServer();
        await server.StartAsync();
        var (host, client, _, _) = await StartAppAsync(server, cancellationToken: ct);
        using var disposableHost = host;
        var settings = SettingsFor(server);
        var cachedKidToken = server.MintToken(settings);

        // Warm the configuration with one genuinely successful request first.
        var warmup = await client.SendAsync(ProtectedRequest(cachedKidToken), ct);
        Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        var fetchesAfterWarmup = server.TotalRequestCount;
        Assert.True(fetchesAfterWarmup > 0);

        // The IdP now faults, and a flood of forged tokens with random, never-published kids
        // arrives -- with RefreshOnIssuerKeyNotFound=false (EntraAuthentication.ConfigureJwtBearer),
        // JwtBearerHandler.RecordTokenValidationError no longer calls
        // Options.ConfigurationManager.RequestRefresh() for a SecurityTokenSignatureKeyNotFoundException
        // at all, so the whole flood should cost the IdP zero extra fetches -- a stricter bound
        // than the "at most one" this assertion still checks for, which is what IdentityModel's
        // own un-throttled-first-call RequestRefresh quirk would otherwise have allowed through.
        server.Mode = ServerMode.Fail;
        for (var i = 0; i < 25; i++)
        {
            var forged = server.MintToken(settings, kidOverride: $"forged-unknown-kid-{i}");
            var response = await client.SendAsync(ProtectedRequest(forged), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.True(
            server.TotalRequestCount - fetchesAfterWarmup <= 1,
            $"expected at most one extra fetch from the forged-kid flood, saw {server.TotalRequestCount - fetchesAfterWarmup}");

        // The cached-kid token must still succeed -- its signing key is already held in the warm
        // configuration and needs no network call at all, regardless of the IdP's current fault or
        // however many unrelated forged-kid lookups just failed.
        var afterFlood = await client.SendAsync(ProtectedRequest(cachedKidToken), ct);
        Assert.Equal(HttpStatusCode.OK, afterFlood.StatusCode);
    }

    [Fact]
    public async Task KeyRotation_RotatedOutSigningKeyIsRejectedAfterLastKnownGoodLifetimeElapses()
    {
        // #226 Rick's review, fix #3 (LOW-MED) pipeline proof: IdentityModel's Last-Known-Good
        // fallback (BaseConfigurationManager.LastKnownGoodConfiguration/LastKnownGoodLifetime)
        // lets a rotated-out signing key keep validating for up to LastKnownGoodLifetime after
        // the IdP stops publishing it -- entra_auth.py has no such grace period. This test proves
        // the SHRUNK window (ConfigureJwtBearer's lastKnownGoodLifetime override, mirroring the
        // production 300s default at a test-friendly scale) actually bounds that grace period,
        // rather than merely asserting the property is set (CooldownAwareConfigurationManagerTests
        // already pins the production 300s default separately -- see
        // CooldownAwareConfigurationManagerTests.ConfigureJwtBearer_DefaultsLastKnownGoodLifetimeTo300Seconds).
        var ct = TestContext.Current.CancellationToken;
        var lkgLifetime = TimeSpan.FromMilliseconds(300);
        await using var server = new FakeDiscoveryServer();
        await server.StartAsync();
        var (host, client, _, configurationManager) = await StartAppAsync(server, lastKnownGoodLifetime: lkgLifetime, cancellationToken: ct);
        using var disposableHost = host;
        var settings = SettingsFor(server);

        // Warm up with the ORIGINAL key -- this is also the moment IdentityModel stamps
        // LastKnownGoodConfiguration (and starts its lifetime clock) against the config this
        // token validated against.
        var originalToken = server.MintToken(settings);
        var warmup = await client.SendAsync(ProtectedRequest(originalToken), ct);
        Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);

        // The IdP rotates: a brand new key/kid is now published; the OLD key/kid is retained only
        // by this test, to keep minting "stale cached client" tokens with it.
        var (oldKey, oldKid) = server.RotateKey();

        // Force the inner ConfigurationManager<T> to re-fetch immediately rather than waiting out
        // the real 12-hour AutomaticRefreshInterval -- RequestRefresh's own RefreshInterval throttle
        // (5-minute default) does not apply to its first-ever call in this manager's lifetime (see
        // ConfigurationManager<T>.RequestRefreshBackgroundThread's _isFirstRefreshRequest bypass),
        // so this call reliably kicks off an immediate, unthrottled background re-fetch exactly
        // once, the same quirk EntraAuthentication.ConfigureJwtBearer's RefreshOnIssuerKeyNotFound
        // = false setting exists to stop JwtBearerHandler from triggering unbounded on every
        // unknown-kid failure.
        configurationManager.RequestRefresh();

        // The refresh runs on a detached background Task.Run. Polling the fake IdP's HTTP request
        // counter is NOT enough: there is a real window between the HTTP response completing and
        // ConfigurationManager<T> actually swapping in the parsed configuration (_currentConfiguration),
        // which under load is wide enough to race and flake this test. Instead, poll the
        // authoritative state directly -- GetBaseConfigurationAsync's own cached result -- until it
        // reflects the rotated key, which is also exactly what the real validation path consults.
        var newKid = server.CurrentKid;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        OpenIdConnectConfiguration? refreshedConfiguration = null;
        while (DateTime.UtcNow < deadline)
        {
            refreshedConfiguration = (OpenIdConnectConfiguration)await configurationManager.GetBaseConfigurationAsync(ct);
            if (refreshedConfiguration.SigningKeys.Any(k => k.KeyId == newKid))
            {
                break;
            }

            await Task.Delay(25, ct);
        }

        Assert.True(
            refreshedConfiguration is not null && refreshedConfiguration.SigningKeys.Any(k => k.KeyId == newKid),
            "expected RequestRefresh() to converge on a configuration whose signing keys include the rotated key");

        // The configuration manager has now picked up the new key: a fresh token signed with it
        // validates immediately.
        var rotatedToken = server.MintToken(settings);
        var afterRotation = await client.SendAsync(ProtectedRequest(rotatedToken), ct);
        Assert.Equal(HttpStatusCode.OK, afterRotation.StatusCode);

        // Sleep comfortably past the (test-shrunk) LastKnownGoodLifetime, anchored at warmup's
        // successful validation above -- by now the OLD key's grace window has unambiguously
        // elapsed (not a tight race against lkgLifetime's own boundary).
        await Task.Delay(lkgLifetime + TimeSpan.FromMilliseconds(700), ct);

        // The OLD, rotated-out key must now be rejected: it isn't in the current configuration
        // (replaced by rotation), and its Last-Known-Good grace period (anchored at warmup, not at
        // rotation time) has elapsed. Without fix #3 (i.e. at IdentityModel's 1-hour default
        // LastKnownGoodLifetime), this same request would still succeed.
        var staleToken = server.MintToken(settings, kidOverride: oldKid, signingKeyOverride: oldKey);
        var afterLkgExpiry = await client.SendAsync(ProtectedRequest(staleToken), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLkgExpiry.StatusCode);
    }
}

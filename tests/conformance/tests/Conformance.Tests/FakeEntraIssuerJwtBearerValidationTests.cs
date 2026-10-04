using System.Net;
using System.Net.Http.Headers;
using System.Linq;
using Conformance.Fakes;
using Conformance.Tests.Scenarios.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #143's own final acceptance item: "Harness unit tests proving the fake issuer's
/// discovery, JWKS and minted tokens validate with stock JwtBearer (C#) and PyJWT (Python)."
///
/// This is the C# half. It hosts a genuinely separate, minimal ASP.NET Core app -- stock
/// <c>Microsoft.AspNetCore.Authentication.JwtBearer</c> middleware, NOT Microsoft.Identity.Web,
/// matching ADR-002's own guidance to validate against the plain OIDC contract rather than a
/// Microsoft-specific SDK -- with <c>Authority</c> pointed at <see cref="FakeEntraIssuer.Issuer"/>.
/// JwtBearer's default <c>MetadataAddress</c> convention is exactly
/// <c>{Authority}/.well-known/openid-configuration</c>, which happens to line up precisely with
/// <see cref="FakeEntraIssuer"/>'s own route (`/{tenant}/v2.0/.well-known/openid-configuration`)
/// with no extra configuration needed -- itself a small proof that the fake's discovery document
/// shape is realistic. From there JwtBearer fetches the fake's real discovery document over real
/// HTTP, then the real JWKS, then validates a real minted token against it: nothing here is
/// mocked or special-cased for the test.
///
/// This is a genuinely different, separate ASP.NET Core host from any conformance backend under
/// test -- it exists solely to prove the fake issuer itself is a faithful OIDC/JWKS implementation,
/// independent of whichever backend (#144/#147) eventually wires JwtBearer up for real.
/// </summary>
[Trait("Category", "Harness")]
public sealed class FakeEntraIssuerJwtBearerValidationTests
{
    private static async Task<(FakeEntraIssuer Issuer, WebApplication App, HttpClient Client)> StartAsync(
        CancellationToken cancellationToken, TimeSpan? clockSkew = null)
    {
        var issuer = new FakeEntraIssuer();
        await issuer.StartAsync(cancellationToken).ConfigureAwait(false);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Stock JwtBearer, stock conventions -- no manual metadata document, no manual
                // JWKS fetch, no custom IssuerSigningKeyResolver: exactly what a real backend
                // wiring ADR-002 up "properly" would do.
                options.Authority = issuer.Issuer;
                options.Audience = FakeEntraIssuer.DefaultClientId;
                options.RequireHttpsMetadata = false; // loopback fake, never a real Entra host
                // R2 (Rick's PR #158 round 1 review): JwtBearer's own default ClockSkew is
                // already 5 minutes, matching persona-architecture.md 18.4 -- explicit only so a
                // caller can also host with a DIFFERENT skew (2 minutes below) to prove the
                // 6b-inside/6b-outside/6c-inside/6c-outside rows actually pin the 5-minute value,
                // rather than merely testing "some skew exists".
                if (clockSkew is not null)
                {
                    options.TokenValidationParameters.ClockSkew = clockSkew.Value;
                }
            });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/secure", () => Results.Ok()).RequireAuthorization();

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (issuer, app, client);
    }

    private static async Task StopAsync(FakeEntraIssuer issuer, WebApplication app, HttpClient client)
    {
        client.Dispose();
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
        await issuer.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task Valid_minted_token_is_accepted_by_stock_JwtBearer()
    {
        var ct = TestContext.Current.CancellationToken;
        var (issuer, app, client) = await StartAsync(ct);
        try
        {
            var token = issuer.Mint();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.GetAsync("/secure", ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await StopAsync(issuer, app, client);
        }
    }

    [Fact]
    public async Task Token_signed_by_the_unpublished_key_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (issuer, app, client) = await StartAsync(ct);
        try
        {
            var token = issuer.Mint(new FakeEntraTokenOverrides { Key = FakeEntraSigningKey.Unpublished });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.GetAsync("/secure", ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await StopAsync(issuer, app, client);
        }
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (issuer, app, client) = await StartAsync(ct);
        try
        {
            var token = issuer.Mint(new FakeEntraTokenOverrides
            {
                Exp = DateTimeOffset.UtcNow.AddMinutes(-20),
                Nbf = DateTimeOffset.UtcNow.AddMinutes(-30),
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.GetAsync("/secure", ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await StopAsync(issuer, app, client);
        }
    }

    [Fact]
    public async Task Wrong_audience_token_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (issuer, app, client) = await StartAsync(ct);
        try
        {
            var token = issuer.Mint(new FakeEntraTokenOverrides { Audience = "99999999-9999-9999-9999-999999999999" });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.GetAsync("/secure", ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await StopAsync(issuer, app, client);
        }
    }

    [Fact]
    public async Task No_token_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (issuer, app, client) = await StartAsync(ct);
        try
        {
            using var response = await client.GetAsync("/secure", ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await StopAsync(issuer, app, client);
        }
    }

    /// <summary>R2 pin (Rick's PR #158 round 1 review): 18.4 sets the clock skew at 5 minutes for
    /// both `exp` and `nbf`. Hosted with that default skew, the boundary pair on each side must
    /// land exactly where 18.11 rows 6b/6c say: 4m30s inside is accepted, 5m30s outside is
    /// rejected.</summary>
    [Theory]
    [InlineData("6b-inside", HttpStatusCode.OK)]
    [InlineData("6b-outside", HttpStatusCode.Unauthorized)]
    [InlineData("6c-inside", HttpStatusCode.OK)]
    [InlineData("6c-outside", HttpStatusCode.Unauthorized)]
    public async Task Clock_skew_boundary_row_at_the_five_minute_skew(string rowName, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var (issuer, app, client) = await StartAsync(ct, clockSkew: TimeSpan.FromMinutes(5));
        try
        {
            var row = AuthRowTokenCase.All.Single(r => r.Row == rowName);
            var token = row.MintToken(issuer);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.GetAsync("/secure", ct);

            Assert.Equal(expected, response.StatusCode);
        }
        finally
        {
            await StopAsync(issuer, app, client);
        }
    }

    /// <summary>R2 pin (Rick's PR #158 round 1 review): the same "inside" token that the 5-minute
    /// skew accepts above must be rejected against a backend configured with a DIFFERENT (2
    /// minute) skew -- proving 6b-inside genuinely pins the 5-minute value from 18.4, rather than
    /// merely proving some skew, of any size, exists.</summary>
    [Fact]
    public async Task Six_b_inside_is_rejected_with_a_two_minute_skew()
    {
        var ct = TestContext.Current.CancellationToken;
        var (issuer, app, client) = await StartAsync(ct, clockSkew: TimeSpan.FromMinutes(2));
        try
        {
            var row = AuthRowTokenCase.All.Single(r => r.Row == "6b-inside");
            var token = row.MintToken(issuer);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.GetAsync("/secure", ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await StopAsync(issuer, app, client);
        }
    }

    /// <summary>R5 pin (Rick's PR #158 round 1 review): a token that carries the real, resolvable
    /// published kid but is either badly signed, unsigned (`alg: none`), or signed with the wrong
    /// algorithm family (HS256) must still be rejected by stock JwtBearer -- proving the
    /// alg/signature check actually runs, rather than the token merely failing at key lookup
    /// (which the pre-existing unknown-kid case above already covers, and which R5's own header-
    /// decode pin in AuthRowCasesTests separately confirms these three shapes do NOT hit).</summary>
    [Theory]
    [InlineData("7 (bad signature, published kid)")]
    [InlineData("7 (alg: none, published kid)")]
    [InlineData("7 (HS256, published kid)")]
    public async Task Published_kid_row_7_variant_is_rejected_by_stock_JwtBearer(string rowName)
    {
        var ct = TestContext.Current.CancellationToken;
        var (issuer, app, client) = await StartAsync(ct);
        try
        {
            var row = AuthRowTokenCase.All.Single(r => r.Row == rowName);
            var token = row.MintToken(issuer);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.GetAsync("/secure", ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await StopAsync(issuer, app, client);
        }
    }
}

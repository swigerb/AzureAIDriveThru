using System.Net;
using System.Net.Http.Headers;
using Conformance.Fakes;
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
public sealed class FakeEntraIssuerJwtBearerValidationTests
{
    private static async Task<(FakeEntraIssuer Issuer, WebApplication App, HttpClient Client)> StartAsync(
        CancellationToken cancellationToken)
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
}

using System.Net;
using Backend.Auth;
using Backend.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Backend.Tests.Http;

/// <summary>
/// Pipeline-level tests for <see cref="FaviconEndpoint"/> against the app's own Entra wiring
/// (<see cref="EntraAuthentication.AddEntraAuthentication"/>: the real JwtBearer scheme plus the
/// fallback authorization policy) on a TestServer. The fallback policy also covers requests that
/// match no endpoint at all, which is exactly why an unmapped <c>/favicon.ico</c> used to answer
/// 401 -- so these tests assert the anonymous 204 AND that nothing else became anonymous.
/// </summary>
public sealed class FaviconEndpointTests
{
    private const string UnmatchedPath = "/robots.txt";

    private const string ProtectedPath = "/api/protected";

    private static async Task<IHost> StartEntraHostAsync(bool mapFavicon, CancellationToken cancellationToken)
    {
        // Loopback instance: an anonymous request never triggers OIDC discovery, and if one ever
        // did it must fail fast against a closed port instead of reaching the real Entra endpoint.
        var settings = new EntraSettings(
            EntraMode.Entra,
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            "access_as_user",
            "DriveThru.User",
            "http://127.0.0.1:1/");
        IHostBuilder hostBuilder = new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddRouting();
                EntraAuthentication.AddEntraAuthentication(services, settings);
            });
            webHost.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    if (mapFavicon)
                    {
                        FaviconEndpoint.Map(endpoints);
                    }

                    endpoints.MapGet(ProtectedPath, () => Results.Ok());
                });
            });
        });
        return await hostBuilder.StartAsync(cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAnonymousAsync(IHost host, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, path);
        return await host.GetTestClient().SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task Get_IsAnonymousNoContent_WithADayLongCacheLifetime()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IHost host = await StartEntraHostAsync(mapFavicon: true, ct);

        using HttpResponseMessage response = await SendAnonymousAsync(host, HttpMethod.Get, FaviconEndpoint.Route, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(FaviconEndpoint.CacheControlValue, response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Head_IsAnonymousNoContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IHost host = await StartEntraHostAsync(mapFavicon: true, ct);

        using HttpResponseMessage response = await SendAnonymousAsync(host, HttpMethod.Head, FaviconEndpoint.Route, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task WithoutTheEndpoint_TheFallbackPolicyAnswersTheProbeWith401()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IHost host = await StartEntraHostAsync(mapFavicon: false, ct);

        using HttpResponseMessage response = await SendAnonymousAsync(host, HttpMethod.Get, FaviconEndpoint.Route, ct);

        // Negative control: proves this harness reproduces the original console error, so the
        // 204 assertions above cannot pass vacuously.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(UnmatchedPath)]
    [InlineData(ProtectedPath)]
    [InlineData("/favicon.ico.json")]
    [InlineData("/favicon.ico/extra")]
    public async Task EveryOtherUnauthenticatedPath_StillGets401(string path)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IHost host = await StartEntraHostAsync(mapFavicon: true, ct);

        using HttpResponseMessage response = await SendAnonymousAsync(host, HttpMethod.Get, path, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

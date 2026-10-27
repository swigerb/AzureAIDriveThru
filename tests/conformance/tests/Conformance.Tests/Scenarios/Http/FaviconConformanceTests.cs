using System.Net;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// The browser's automatic <c>/favicon.ico</c> probe gets an empty <c>204</c> instead of the 401 that
/// Entra mode's fallback authorization policy gives every request that matches no endpoint (see
/// <c>Backend/Http/FaviconEndpoint.cs</c>), and nothing else is opened up by it.
///
/// Black-box on purpose: <c>Backend.Tests</c> proves the endpoint inside a TestServer with its own
/// copy of the auth wiring, but nothing there boots <c>Program.cs</c>, so only a real launch notices
/// the <c>FaviconEndpoint.Map</c> call being dropped.
///
/// C#-only: the python backend has no <c>/favicon.ico</c> route, so both rows skip on the python leg.
/// </summary>
[Collection(ConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class FaviconConformanceTests(ConformanceFixture fixture)
{
    private const string CSharpOnly =
        "C#-only (Backend/Http/FaviconEndpoint.cs); the python backend has no /favicon.ico route.";

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task An_anonymous_GET_or_HEAD_of_the_favicon_gets_an_empty_204_that_may_be_cached() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var anonymous = new HttpClient();
        Uri favicon = new(fixture.Backend!.BaseUri, "/favicon.ico");

        foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var request = new HttpRequestMessage(method, favicon);
            using HttpResponseMessage response = await anonymous.SendAsync(request, ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.NoContent,
                $"Anonymous {method} /favicon.ico: expected 204, got {(int)response.StatusCode}.");
            Assert.Equal("public, max-age=86400", response.Headers.CacheControl?.ToString());
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        }
    });

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task Only_the_exact_favicon_route_is_open_so_its_neighbours_still_answer_401() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var anonymous = new HttpClient();

        foreach (string path in new[] { "/favicon.ico/extra", "/favicon.ico.json", "/favicon.png", "/api/personas" })
        {
            using HttpResponseMessage response = await anonymous.GetAsync(new Uri(fixture.Backend!.BaseUri, path), ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"Anonymous GET {path}: expected 401, got {(int)response.StatusCode}. Only /favicon.ico itself may be anonymous.");
        }
    });
}

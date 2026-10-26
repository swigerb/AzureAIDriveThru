using System.Net;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Every answer the C# backend gives carries the browser-hardening headers from
/// <c>Backend/Http/SecurityHeaders.cs</c> (nosniff, framing, referrer, permissions, a
/// Content-Security-Policy and, behind an HTTPS ingress, HSTS), and the process never advertises its
/// server software.
///
/// Black-box on purpose. <c>Backend.Tests</c> pins the exact policy text and the middleware's
/// behaviour inside a TestServer; only a real launch proves that <c>Program.cs</c> installs it in
/// front of static files and the auth fallback policy (so 401s and 404s are covered too) and that
/// Kestrel's own <c>Server</c> header is off. The rows assert the security-relevant invariants of the
/// policy rather than its whole string, so a deliberate allowance edit does not need a change here.
///
/// C#-only: the Python backend does not emit these headers (it sets <c>X-Content-Type-Options</c> on
/// persona assets and nothing else), so every row skips on the python leg.
/// </summary>
[Collection(ConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class SecurityHeadersConformanceTests(ConformanceFixture fixture)
{
    private const string CSharpOnly =
        "C#-only hardening (Backend/Http/SecurityHeaders.cs); the python backend does not emit these headers.";

    private const string ContentSecurityPolicy = "Content-Security-Policy";

    private const string StrictTransportSecurity = "Strict-Transport-Security";

    private const string EntraOrigin = "https://login.microsoftonline.com";

    private static readonly (string Path, HttpStatusCode Expected)[] s_authenticatedResponses =
    [
        ("/health", HttpStatusCode.OK),
        ("/", HttpStatusCode.OK),
        ("/no-such-route", HttpStatusCode.NotFound),
    ];

    private static readonly string[] s_anonymousPaths = ["/api/personas", "/no-such-route"];

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task Health_the_index_and_an_unknown_route_all_carry_the_baseline_headers() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient http = ConformanceHttpClient.Create();

        foreach ((string path, HttpStatusCode expected) in s_authenticatedResponses)
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, path), ct);

            Assert.Equal(expected, response.StatusCode);
            AssertBaselineHeaders(response, $"GET {path}");
        }
    });

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task A_401_from_the_auth_fallback_policy_carries_the_headers_too() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var anonymous = new HttpClient();

        foreach (string path in s_anonymousPaths)
        {
            using HttpResponseMessage response = await anonymous.GetAsync(new Uri(fixture.Backend!.BaseUri, path), ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            AssertBaselineHeaders(response, $"anonymous GET {path}");
        }
    });

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task The_policy_denies_scripts_plugins_and_foreign_framing_and_allows_only_what_the_app_uses() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient http = ConformanceHttpClient.Create();
        using HttpResponseMessage response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/"), ct);

        Dictionary<string, string[]> policy = ParsePolicy(Single(response, ContentSecurityPolicy, "GET /"));

        Assert.Equal(["'self'"], policy["default-src"]);
        Assert.Equal(["'self'"], policy["script-src"]);
        Assert.Equal(["'none'"], policy["object-src"]);
        Assert.Equal(["'self'"], policy["base-uri"]);
        Assert.Equal(["'self'"], policy["form-action"]);
        Assert.Equal(["'self'"], policy["frame-ancestors"]);
        Assert.Equal(["'self'", EntraOrigin], policy["frame-src"]);
        Assert.Equal(["'self'", $"ws://{fixture.Backend!.BaseUri.Authority}", EntraOrigin], policy["connect-src"]);

        string[] everySource = [.. policy.Values.SelectMany(sources => sources)];
        Assert.DoesNotContain("'unsafe-eval'", everySource);
        Assert.DoesNotContain("*", everySource);
        Assert.DoesNotContain("http:", everySource);
        Assert.DoesNotContain("https:", everySource);
    });

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task Plain_http_gets_neither_HSTS_nor_a_wss_origin_and_a_forwarded_https_request_gets_both() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Uri health = new(fixture.Backend!.BaseUri, "/health");
        string authority = fixture.Backend!.BaseUri.Authority;
        using HttpClient http = ConformanceHttpClient.Create();

        using HttpResponseMessage plain = await http.GetAsync(health, ct);
        Assert.False(plain.Headers.Contains(StrictTransportSecurity),
            "HSTS must not be sent for a request that arrived over plain HTTP: browsers ignore it there and it would be noise.");
        Assert.Equal(
            ["'self'", $"ws://{authority}", EntraOrigin],
            ParsePolicy(Single(plain, ContentSecurityPolicy, "plain GET /health"))["connect-src"]);

        using var forwarded = new HttpRequestMessage(HttpMethod.Get, health);
        forwarded.Headers.Add("X-Forwarded-Proto", "https");
        using HttpResponseMessage secure = await http.SendAsync(forwarded, ct);
        Assert.Equal("max-age=31536000; includeSubDomains", Single(secure, StrictTransportSecurity, "forwarded-https GET /health"));
        Assert.Equal(
            ["'self'", $"wss://{authority}", EntraOrigin],
            ParsePolicy(Single(secure, ContentSecurityPolicy, "forwarded-https GET /health"))["connect-src"]);
    });

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task The_server_software_is_not_advertised() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient authenticated = ConformanceHttpClient.Create();
        using var anonymous = new HttpClient();

        foreach (HttpClient http in new[] { authenticated, anonymous })
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/health"), ct);

            Assert.False(response.Headers.Contains("Server"), "The Server header names the web server and version; it must be off.");
            Assert.False(response.Headers.Contains("X-Powered-By"), "X-Powered-By must not be sent.");
        }
    });

    private static void AssertBaselineHeaders(HttpResponseMessage response, string what)
    {
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options", what));
        Assert.Equal("SAMEORIGIN", Single(response, "X-Frame-Options", what));
        Assert.Equal("strict-origin-when-cross-origin", Single(response, "Referrer-Policy", what));

        string permissions = Single(response, "Permissions-Policy", what);
        Assert.Contains("microphone=(self)", permissions, StringComparison.Ordinal);
        Assert.Contains("camera=()", permissions, StringComparison.Ordinal);

        string policy = Single(response, ContentSecurityPolicy, what);
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'self'", policy, StringComparison.Ordinal);
    }

    /// <summary>The one value of a response header, failing (naming the request) when it is missing or sent more than once.</summary>
    private static string Single(HttpResponseMessage response, string name, string what)
    {
        string[] values = response.Headers.TryGetValues(name, out IEnumerable<string>? found) ? [.. found] : [];

        Assert.True(values.Length == 1, $"{what}: expected exactly one {name} header, found {values.Length}.");
        return values[0];
    }

    /// <summary>Directive name to its source list. A duplicated directive throws: browsers ignore the second one, so it is a defect.</summary>
    private static Dictionary<string, string[]> ParsePolicy(string policy) =>
        policy
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1..], StringComparer.Ordinal);
}

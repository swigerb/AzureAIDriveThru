using System.Net;
using Backend.Auth;
using Backend.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Backend.Tests.Http;

/// <summary>
/// Tests for <see cref="SecurityHeaders"/>: the reviewed policy itself, the Host-header validation
/// that stands between a request and the policy text, the HSTS gate, and the middleware's ordering
/// against static files, the Entra fallback policy and unmatched routes.
/// </summary>
public sealed class SecurityHeadersTests
{
    private const string ExpectedFallbackPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data:; " +
        "media-src 'self'; " +
        "connect-src 'self' https://login.microsoftonline.com; " +
        "frame-src 'self' https://login.microsoftonline.com; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'self'";

    private const string EntraOrigin = "https://login.microsoftonline.com";

    private const string ProtectedPath = "/api/protected";

    private const string OpenPath = "/open";

    private const string AssetPath = "/asset.js";

    private static List<KeyValuePair<string, string>> ParseDirectives(string policy)
    {
        List<KeyValuePair<string, string>> directives = [];
        foreach (string directive in policy.Split("; "))
        {
            int space = directive.IndexOf(' ');
            directives.Add(space < 0
                ? new KeyValuePair<string, string>(directive, string.Empty)
                : new KeyValuePair<string, string>(directive[..space], directive[(space + 1)..]));
        }

        return directives;
    }

    private static string DirectiveValue(string policy, string name) =>
        ParseDirectives(policy).Single(d => d.Key == name).Value;

    private static async Task<IHeaderDictionary> InvokeAsync(
        bool emitHsts,
        Action<HttpRequest>? configureRequest = null,
        Action<IApplicationBuilder>? configureDownstream = null)
    {
        var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        builder.UseSecurityHeaders(emitHsts);
        configureDownstream?.Invoke(builder);
        RequestDelegate pipeline = builder.Build();

        var context = new DefaultHttpContext();
        configureRequest?.Invoke(context.Request);
        await pipeline(context);
        return context.Response.Headers;
    }

    // ── The reviewed policy ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Fallback_policy_is_exactly_the_reviewed_policy()
    {
        Assert.Equal(ExpectedFallbackPolicy, SecurityHeaders.FallbackContentSecurityPolicy);
        Assert.Equal(ExpectedFallbackPolicy, SecurityHeaders.BuildContentSecurityPolicy(authority: null, secure: true));
    }

    [Fact]
    public void Policy_denies_plugins_foreign_bases_and_foreign_framing()
    {
        string policy = SecurityHeaders.FallbackContentSecurityPolicy;

        Assert.Equal("'self'", DirectiveValue(policy, "default-src"));
        Assert.Equal("'self'", DirectiveValue(policy, "script-src"));
        Assert.Equal("'none'", DirectiveValue(policy, "object-src"));
        Assert.Equal("'self'", DirectiveValue(policy, "base-uri"));
        Assert.Equal("'self'", DirectiveValue(policy, "form-action"));
        Assert.Equal("'self'", DirectiveValue(policy, "frame-ancestors"));
    }

    [Fact]
    public void Policy_has_no_wildcards_no_eval_and_unsafe_inline_only_for_styles()
    {
        string policy = SecurityHeaders.FallbackContentSecurityPolicy;

        Assert.DoesNotContain('*', policy);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("blob:", policy, StringComparison.Ordinal);

        string[] directivesWithUnsafeInline = [.. ParseDirectives(policy)
            .Where(d => d.Value.Contains("'unsafe-inline'", StringComparison.Ordinal))
            .Select(d => d.Key)];
        Assert.Equal(["style-src"], directivesWithUnsafeInline);
    }

    [Fact]
    public void Policy_allows_data_uris_only_for_images()
    {
        string[] directivesWithData = [.. ParseDirectives(SecurityHeaders.FallbackContentSecurityPolicy)
            .Where(d => d.Value.Contains("data:", StringComparison.Ordinal))
            .Select(d => d.Key)];

        Assert.Equal(["img-src"], directivesWithData);
    }

    [Fact]
    public void Policy_reaches_only_the_reviewed_external_origins()
    {
        string[] origins = [.. ParseDirectives(SecurityHeaders.FallbackContentSecurityPolicy)
            .SelectMany(d => d.Value.Split(' '))
            .Where(token => token.Contains("://", StringComparison.Ordinal))
            .Distinct()
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            ["https://fonts.googleapis.com", "https://fonts.gstatic.com", EntraOrigin],
            origins);
    }

    // ── Host header → connect-src ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("example.com")]
    [InlineData("localhost")]
    [InlineData("localhost:5000")]
    [InlineData("127.0.0.1:8000")]
    [InlineData("A.b-C.example.com:65535")]
    [InlineData("capps-dotnet-pwvzk3t22wttm.orangeisland-69fbd3a0.eastus2.azurecontainerapps.io")]
    public void Connect_src_names_the_request_host_with_the_matching_websocket_scheme(string authority)
    {
        string secure = SecurityHeaders.BuildContentSecurityPolicy(authority, secure: true);
        string plain = SecurityHeaders.BuildContentSecurityPolicy(authority, secure: false);

        Assert.Equal($"'self' wss://{authority} {EntraOrigin}", DirectiveValue(secure, "connect-src"));
        Assert.Equal($"'self' ws://{authority} {EntraOrigin}", DirectiveValue(plain, "connect-src"));
    }

    [Fact]
    public void A_trusted_host_changes_connect_src_and_nothing_else()
    {
        List<KeyValuePair<string, string>> fallback = ParseDirectives(SecurityHeaders.FallbackContentSecurityPolicy);
        List<KeyValuePair<string, string>> withHost = ParseDirectives(SecurityHeaders.BuildContentSecurityPolicy("example.com", secure: true));

        Assert.Equal(fallback.Select(d => d.Key), withHost.Select(d => d.Key));
        string[] changed = [.. fallback
            .Zip(withHost, (before, after) => (before.Key, Same: before.Value == after.Value))
            .Where(pair => !pair.Same)
            .Select(pair => pair.Key)];
        Assert.Equal(["connect-src"], changed);
    }

    [Theory]
    [InlineData("evil.com;script-src *")]
    [InlineData("evil.com script-src *")]
    [InlineData("evil.com,*")]
    [InlineData("ev'il.com")]
    [InlineData("evil.com'")]
    [InlineData("*")]
    [InlineData("*.example.com")]
    [InlineData("example.com/path")]
    [InlineData("user@example.com")]
    [InlineData("example.com?x=1")]
    [InlineData("example.com#frag")]
    [InlineData("exa_mple.com")]
    [InlineData("example.com.")]
    [InlineData(".example.com")]
    [InlineData("exa..mple.com")]
    [InlineData("[::1]")]
    [InlineData("[::1]:8080")]
    [InlineData("::1")]
    [InlineData("example.com:")]
    [InlineData(":8080")]
    [InlineData("example.com:abc")]
    [InlineData("example.com:0")]
    [InlineData("example.com:65536")]
    [InlineData("example.com:123456")]
    [InlineData("example.com:80:80")]
    [InlineData("example.com:-1")]
    [InlineData("example.com:+80")]
    [InlineData("example.com: 80")]
    [InlineData(" example.com")]
    [InlineData("example.com ")]
    [InlineData("")]
    public void Hostile_or_malformed_host_takes_the_fallback_policy(string authority)
    {
        Assert.False(SecurityHeaders.IsSafeAuthority(authority));
        Assert.Equal(ExpectedFallbackPolicy, SecurityHeaders.BuildContentSecurityPolicy(authority, secure: true));
        Assert.Equal(ExpectedFallbackPolicy, SecurityHeaders.BuildContentSecurityPolicy(authority, secure: false));
    }

    [Fact]
    public void Host_with_control_or_non_ascii_characters_takes_the_fallback_policy()
    {
        string[] hostile =
        [
            "example.com\r\nSet-Cookie: x=1",
            "example.com\n",
            "example.com\0",
            "example.com\t",
            "\u0000",
            "ex\u00e4mple.com",
            "example\uFF0Ecom",
            "\u202Eexample.com",
            "example.com\u2028",
            "\u0661\u0662\u0663.example.com",
            "example.com:\u0661\u0662",
        ];

        foreach (string authority in hostile)
        {
            Assert.False(SecurityHeaders.IsSafeAuthority(authority), $"accepted a hostile host: {authority.Length} chars, first U+{(int)authority[0]:X4}");
            Assert.Equal(ExpectedFallbackPolicy, SecurityHeaders.BuildContentSecurityPolicy(authority, secure: true));
        }
    }

    [Fact]
    public void Only_letters_digits_hyphen_and_dot_survive_inside_a_host()
    {
        // Kestrel passes 72 ASCII characters through to the app (checked against a real process on
        // 11.0.100-rc.3), including ' ! $ & ( ) _ ~. Exactly these 64 may reach the policy.
        const string Expected = "-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        char[] accepted = [.. Enumerable.Range(0, 128)
            .Select(code => (char)code)
            .Where(c => SecurityHeaders.IsSafeAuthority($"a{c}b"))];

        Assert.Equal(Expected, new string(accepted));
    }

    [Fact]
    public void Missing_host_takes_the_fallback_policy()
    {
        Assert.False(SecurityHeaders.IsSafeAuthority(null));
        Assert.Equal(ExpectedFallbackPolicy, SecurityHeaders.BuildContentSecurityPolicy(null, secure: true));
    }

    [Fact]
    public void Host_length_limits_sit_exactly_at_the_dns_maximum()
    {
        string longest = new('a', 253);

        Assert.True(SecurityHeaders.IsSafeAuthority(longest));
        Assert.True(SecurityHeaders.IsSafeAuthority(longest + ":65535"));
        Assert.False(SecurityHeaders.IsSafeAuthority(longest + "a"));
        Assert.False(SecurityHeaders.IsSafeAuthority(longest + "a:65535"));
        Assert.False(SecurityHeaders.IsSafeAuthority(new string('a', 4096)));
    }

    [Fact]
    public void Port_limits_sit_exactly_at_the_tcp_range()
    {
        Assert.True(SecurityHeaders.IsSafeAuthority("example.com:1"));
        Assert.True(SecurityHeaders.IsSafeAuthority("example.com:65535"));
        Assert.False(SecurityHeaders.IsSafeAuthority("example.com:0"));
        Assert.False(SecurityHeaders.IsSafeAuthority("example.com:65536"));
    }

    // ── Headers on a response ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_baseline_header_is_set_with_its_reviewed_value()
    {
        IHeaderDictionary headers = await InvokeAsync(
            emitHsts: true,
            request => request.Host = new HostString("example.com"));

        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
        Assert.Equal("SAMEORIGIN", headers.XFrameOptions.ToString());
        Assert.Equal("strict-origin-when-cross-origin", headers["Referrer-Policy"].ToString());
        Assert.Equal("camera=(), geolocation=(), microphone=(self), payment=(), usb=()", headers["Permissions-Policy"].ToString());
        Assert.Equal(
            SecurityHeaders.BuildContentSecurityPolicy("example.com", secure: false),
            headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public async Task Permissions_policy_keeps_the_microphone_for_this_origin_and_denies_the_rest()
    {
        IHeaderDictionary headers = await InvokeAsync(emitHsts: false);

        string policy = headers["Permissions-Policy"].ToString();
        Assert.Contains("microphone=(self)", policy, StringComparison.Ordinal);
        Assert.Contains("camera=()", policy, StringComparison.Ordinal);
        Assert.Contains("geolocation=()", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_request_host_and_port_reach_connect_src()
    {
        IHeaderDictionary headers = await InvokeAsync(
            emitHsts: false,
            request => request.Host = new HostString("localhost", 5000));

        Assert.Equal(
            $"'self' ws://localhost:5000 {EntraOrigin}",
            DirectiveValue(headers.ContentSecurityPolicy.ToString(), "connect-src"));
    }

    [Fact]
    public async Task A_hostile_host_header_cannot_add_directives()
    {
        IHeaderDictionary headers = await InvokeAsync(
            emitHsts: true,
            request => request.Host = new HostString("evil.com; script-src *"));

        Assert.Equal(ExpectedFallbackPolicy, headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public async Task A_route_that_sets_a_header_itself_replaces_the_value_instead_of_duplicating_it()
    {
        IHeaderDictionary headers = await InvokeAsync(
            emitHsts: false,
            configureDownstream: app => app.Use(next => context =>
            {
                context.Response.Headers.XFrameOptions = "DENY";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return next(context);
            }));

        Assert.Single(headers.XFrameOptions);
        Assert.Equal("DENY", headers.XFrameOptions.ToString());
        Assert.Single(headers.XContentTypeOptions);
    }

    // ── HSTS and the secure-request signal ────────────────────────────────────────────────────

    [Fact]
    public async Task Hsts_is_not_sent_over_plain_http()
    {
        IHeaderDictionary headers = await InvokeAsync(emitHsts: true);

        Assert.False(headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_is_sent_for_a_request_that_is_https_itself_with_a_year_and_no_preload()
    {
        IHeaderDictionary headers = await InvokeAsync(emitHsts: true, request => request.IsHttps = true);

        string hsts = headers.StrictTransportSecurity.ToString();
        Assert.Equal("max-age=31536000; includeSubDomains", hsts);
        Assert.DoesNotContain("preload", hsts, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https")]
    [InlineData("HTTPS")]
    [InlineData(" https ")]
    [InlineData("http, https")]
    [InlineData("https,http")]
    public async Task Hsts_is_sent_when_the_request_was_forwarded_as_https(string forwardedProto)
    {
        IHeaderDictionary headers = await InvokeAsync(
            emitHsts: true,
            request => request.Headers["X-Forwarded-Proto"] = forwardedProto);

        Assert.True(headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_is_sent_when_https_is_on_a_later_forwarded_header_line()
    {
        IHeaderDictionary headers = await InvokeAsync(
            emitHsts: true,
            request =>
            {
                request.Headers.Append("X-Forwarded-Proto", "http");
                request.Headers.Append("X-Forwarded-Proto", "https");
            });

        Assert.True(headers.ContainsKey("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("")]
    [InlineData("httpss")]
    [InlineData("xhttps")]
    [InlineData("wss")]
    [InlineData("http, ws")]
    public async Task Hsts_is_not_sent_when_the_request_was_forwarded_as_anything_but_https(string forwardedProto)
    {
        IHeaderDictionary headers = await InvokeAsync(
            emitHsts: true,
            request => request.Headers["X-Forwarded-Proto"] = forwardedProto);

        Assert.False(headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Hsts_is_never_sent_when_disabled_even_over_https()
    {
        IHeaderDictionary headers = await InvokeAsync(emitHsts: false, request => request.IsHttps = true);

        Assert.False(headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task A_secure_request_gets_a_wss_connect_source_and_a_plain_one_gets_ws()
    {
        IHeaderDictionary https = await InvokeAsync(
            emitHsts: true,
            request =>
            {
                request.Host = new HostString("example.com");
                request.Headers["X-Forwarded-Proto"] = "https";
            });
        IHeaderDictionary http = await InvokeAsync(
            emitHsts: true,
            request => request.Host = new HostString("example.com"));

        Assert.Equal(
            $"'self' wss://example.com {EntraOrigin}",
            DirectiveValue(https.ContentSecurityPolicy.ToString(), "connect-src"));
        Assert.Equal(
            $"'self' ws://example.com {EntraOrigin}",
            DirectiveValue(http.ContentSecurityPolicy.ToString(), "connect-src"));
    }

    // ── Pipeline ordering on a real host ──────────────────────────────────────────────────────

    private static async Task<IHost> StartHostAsync(bool entra, string? staticRoot, CancellationToken cancellationToken)
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
                if (entra)
                {
                    EntraAuthentication.AddEntraAuthentication(services, settings);
                }
                else
                {
                    services.AddAuthorization();
                }
            });
            webHost.Configure(app =>
            {
                // Same position as Program.cs: first, ahead of UseStaticFiles.
                app.UseSecurityHeaders(emitHsts: true);
                if (staticRoot is not null)
                {
                    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(staticRoot) });
                }

                app.UseRouting();
                if (entra)
                {
                    app.UseAuthentication();
                }

                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapMethods(OpenPath, ["GET", "HEAD"], () => Results.Ok()).AllowAnonymous();
                    endpoints.MapGet(ProtectedPath, () => Results.Ok());
                });
            });
        });
        return await hostBuilder.StartAsync(cancellationToken);
    }

    private static void AssertBaselineHeaders(HttpResponseMessage response)
    {
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(["SAMEORIGIN"], response.Headers.GetValues("X-Frame-Options"));
        Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Single(response.Headers.GetValues("Referrer-Policy"));
        Assert.Single(response.Headers.GetValues("Permissions-Policy"));
    }

    [Theory]
    [InlineData("GET", OpenPath, HttpStatusCode.OK)]
    [InlineData("HEAD", OpenPath, HttpStatusCode.OK)]
    [InlineData("GET", ProtectedPath, HttpStatusCode.Unauthorized)]
    [InlineData("GET", "/no/such/route", HttpStatusCode.Unauthorized)]
    public async Task Entra_mode_answers_carry_the_headers_including_the_401s(string method, string path, HttpStatusCode expected)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IHost host = await StartHostAsync(entra: true, staticRoot: null, ct);

        using HttpRequestMessage request = new(new HttpMethod(method), path);
        using HttpResponseMessage response = await host.GetTestClient().SendAsync(request, ct);

        Assert.Equal(expected, response.StatusCode);
        AssertBaselineHeaders(response);
    }

    [Fact]
    public async Task An_unmatched_route_answers_404_with_the_headers_when_no_auth_is_configured()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IHost host = await StartHostAsync(entra: false, staticRoot: null, ct);

        using HttpRequestMessage request = new(HttpMethod.Get, "/no/such/route");
        using HttpResponseMessage response = await host.GetTestClient().SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertBaselineHeaders(response);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task A_static_file_short_circuits_the_pipeline_yet_still_carries_the_headers(string method)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "dt-security-headers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "asset.js"), "// asset", ct);
            using IHost host = await StartHostAsync(entra: true, root, ct);

            using HttpRequestMessage request = new(new HttpMethod(method), AssetPath);
            using HttpResponseMessage response = await host.GetTestClient().SendAsync(request, ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertBaselineHeaders(response);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_https_request_to_a_real_host_gets_hsts_and_a_wss_connect_source()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IHost host = await StartHostAsync(entra: true, staticRoot: null, ct);
        using HttpClient client = host.GetTestClient();
        client.BaseAddress = new Uri("https://example.com/");

        using HttpResponseMessage response = await client.GetAsync(OpenPath, ct);

        Assert.Equal(["max-age=31536000; includeSubDomains"], response.Headers.GetValues("Strict-Transport-Security"));
        string policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Equal($"'self' wss://example.com {EntraOrigin}", DirectiveValue(policy, "connect-src"));
    }

    [Fact]
    public async Task A_plain_http_request_to_a_real_host_gets_no_hsts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IHost host = await StartHostAsync(entra: true, staticRoot: null, ct);

        using HttpResponseMessage response = await host.GetTestClient().GetAsync(OpenPath, ct);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }
}

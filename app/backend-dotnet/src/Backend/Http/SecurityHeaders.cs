using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Backend.Http;

/// <summary>
/// Browser-hardening response headers for every answer this backend gives: the SPA shell, its static
/// assets, the JSON API, 401s from the Entra fallback policy and 404s alike. Installed as the first
/// middleware (before <c>UseStaticFiles</c>, which short-circuits the pipeline for a file it serves)
/// so no response path escapes it.
/// <para>
/// The headers are written before the rest of the pipeline runs, by indexer assignment, so a route
/// that sets the same header itself (the persona asset route sets <c>nosniff</c>) simply replaces the
/// value instead of producing a duplicate.
/// </para>
/// <para>
/// The Content-Security-Policy is shaped by what the shipped SPA actually does. Every allowance below
/// has a reason; adding a new external origin means editing the constants here and the expected
/// policy in <c>SecurityHeadersTests</c>, which is deliberate friction.
/// </para>
/// <list type="bullet">
/// <item><description><c>script-src 'self'</c>: no inline scripts and no <c>eval</c>. The audio worklets are same-origin files.</description></item>
/// <item><description><c>style-src 'unsafe-inline'</c>: Radix (scroll lock) and the persona theme inject <c>&lt;style&gt;</c> elements at runtime, which a script-free CSP cannot nonce. Styles are a far smaller risk than scripts.</description></item>
/// <item><description>Google Fonts (<c>fonts.googleapis.com</c> for the stylesheet, <c>fonts.gstatic.com</c> for the files): the persona font import.</description></item>
/// <item><description><c>img-src 'self' data:</c>: persona logos are same-origin and the bundle inlines a few <c>data:</c> SVGs. The Sonic menu data also carries <c>https://imagedelivery.net</c> image URLs, but the SPA never renders them; a UI that starts to render them has to add that origin.</description></item>
/// <item><description>Dark Reader is bundled but nothing calls its <c>enable()</c> today (the settings switch only toggles a Tailwind class). If it is wired up, re-test under this policy: it creates <c>blob:</c> URLs and fetches stylesheets, neither of which this policy allows.</description></item>
/// <item><description><c>login.microsoftonline.com</c> in <c>connect-src</c>: MSAL's discovery and token requests.</description></item>
/// <item><description><c>frame-src 'self' login.microsoftonline.com</c> and <c>frame-ancestors 'self'</c>: MSAL's hidden silent-renewal iframe goes to Entra and is redirected back to this origin, which then has to be allowed to load inside the app's own page. For the same reason the legacy header is <c>SAMEORIGIN</c>, not <c>DENY</c>.</description></item>
/// <item><description>An explicit <c>ws://</c>/<c>wss://</c> origin in <c>connect-src</c>: older Safari does not let <c>'self'</c> match WebSocket schemes. It is taken from the Host header, so it is validated strictly (see <see cref="IsSafeAuthority"/>).</description></item>
/// </list>
/// <para>
/// HSTS is emitted only for requests that are, or were forwarded as, HTTPS, and never in Development.
/// The container listens on plain HTTP behind the Container Apps ingress, so <c>X-Forwarded-Proto</c>
/// is the signal. Browsers ignore HSTS received over plain HTTP, so a spoofed forwarded header gains
/// an attacker nothing. <c>includeSubDomains</c> covers subdomains of the serving host only; an
/// operator who serves this app from an apex domain shared with HTTP-only subdomains should know.
/// </para>
/// </summary>
internal static class SecurityHeaders
{
    public const string ContentTypeOptionsValue = "nosniff";

    public const string FrameOptionsValue = "SAMEORIGIN";

    public const string ReferrerPolicyValue = "strict-origin-when-cross-origin";

    public const string PermissionsPolicyValue = "camera=(), geolocation=(), microphone=(self), payment=(), usb=()";

    public const string StrictTransportSecurityValue = "max-age=31536000; includeSubDomains";

    public const string EntraOrigin = "https://login.microsoftonline.com";

    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

    private const string ReferrerPolicyHeader = "Referrer-Policy";

    private const string PermissionsPolicyHeader = "Permissions-Policy";

    private const int MaxAuthorityLength = 253 + 1 + 5;

    private const int MaxHostLength = 253;

    private const string PolicyBeforeConnectSrc =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data:; " +
        "media-src 'self'; " +
        "connect-src 'self' ";

    private const string PolicyAfterConnectSrc =
        "; frame-src 'self' " + EntraOrigin + "; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'self'";

    /// <summary>
    /// The policy when the Host header cannot be trusted as a CSP source: same-origin plus Entra.
    /// Modern browsers still match <c>'self'</c> to the same-origin WebSocket.
    /// </summary>
    public const string FallbackContentSecurityPolicy = PolicyBeforeConnectSrc + EntraOrigin + PolicyAfterConnectSrc;

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool emitHsts)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use((HttpContext context, RequestDelegate next) =>
        {
            Apply(context, emitHsts);
            return next(context);
        });
    }

    internal static void Apply(HttpContext context, bool emitHsts)
    {
        IHeaderDictionary headers = context.Response.Headers;
        bool secure = IsSecureRequest(context.Request);

        headers.XContentTypeOptions = ContentTypeOptionsValue;
        headers.XFrameOptions = FrameOptionsValue;
        headers[ReferrerPolicyHeader] = ReferrerPolicyValue;
        headers[PermissionsPolicyHeader] = PermissionsPolicyValue;
        headers.ContentSecurityPolicy = BuildContentSecurityPolicy(context.Request.Host.Value, secure);

        if (emitHsts && secure)
        {
            headers.StrictTransportSecurity = StrictTransportSecurityValue;
        }
    }

    /// <summary>
    /// Builds the CSP for a request. <paramref name="authority"/> is the raw Host header value; it
    /// reaches the policy only when <see cref="IsSafeAuthority"/> accepts it.
    /// </summary>
    public static string BuildContentSecurityPolicy(string? authority, bool secure) =>
        IsSafeAuthority(authority)
            ? string.Concat(PolicyBeforeConnectSrc, secure ? "wss://" : "ws://", authority, " ", EntraOrigin, PolicyAfterConnectSrc)
            : FallbackContentSecurityPolicy;

    /// <summary>
    /// True only for a DNS name or IPv4 address with an optional <c>:port</c>, i.e. exactly what a CSP
    /// host-source can hold. The Host header is request-controlled text spliced into the policy, and the
    /// server's own Host validation is not CSP-aware: Kestrel (checked on 11.0.100-rc.3) still passes
    /// <c>'</c>, <c>!</c>, <c>$</c>, <c>&amp;</c>, <c>(</c>, <c>)</c>, <c>_</c> and <c>~</c>, and a test host or a
    /// future server version may pass more. Anything outside the grammar takes the fallback policy.
    /// IPv6 literals (brackets) are rejected on purpose and take the fallback policy too.
    /// </summary>
    public static bool IsSafeAuthority(string? authority)
    {
        if (string.IsNullOrEmpty(authority) || authority.Length > MaxAuthorityLength)
        {
            return false;
        }

        ReadOnlySpan<char> value = authority;
        int colon = value.IndexOf(':');
        if (colon < 0)
        {
            return IsSafeHost(value);
        }

        return IsSafeHost(value[..colon]) && IsSafePort(value[(colon + 1)..]);
    }

    private static bool IsSafeHost(ReadOnlySpan<char> host)
    {
        if (host.IsEmpty || host.Length > MaxHostLength)
        {
            return false;
        }

        // A CSP host-part is dot-separated labels of 1+ [A-Za-z0-9-]: no empty label, so no leading,
        // trailing or doubled dot.
        bool previousWasDot = true;
        foreach (char c in host)
        {
            if (c == '.')
            {
                if (previousWasDot)
                {
                    return false;
                }

                previousWasDot = true;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }

            previousWasDot = false;
        }

        return !previousWasDot;
    }

    private static bool IsSafePort(ReadOnlySpan<char> port) =>
        port.Length is >= 1 and <= 5 &&
        int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number) &&
        number is >= 1 and <= 65535;

    private static bool IsSecureRequest(HttpRequest request) =>
        request.IsHttps || ForwardedAsHttps(request.Headers[ForwardedProtoHeader]);

    /// <summary>
    /// True when any comma-separated <c>X-Forwarded-Proto</c> value, on any header line, is
    /// <c>https</c>. Lenient by design: the only consequences of a false positive are an HSTS header
    /// that browsers ignore over HTTP and a <c>wss://</c> source for a socket that is not secure.
    /// </summary>
    private static bool ForwardedAsHttps(StringValues forwarded)
    {
        foreach (string? line in forwarded)
        {
            if (line is null)
            {
                continue;
            }

            foreach (StringSegment value in new StringTokenizer(line, [',']))
            {
                if (value.AsSpan().Trim().Equals("https", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

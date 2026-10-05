using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Auth;

/// <summary>
/// Decorates a real <see cref="BaseConfigurationManager"/> (OpenID Connect discovery/JWKS fetcher)
/// so a genuine, request-awaited cold-start fetch failure (no configuration ever successfully
/// held yet) actually arms <see cref="DiscoveryFailureGate"/> on the REAL pipeline -- #226 Rick's
/// HIGH finding.
///
/// Why the previous wiring never armed the gate in production: ASP.NET Core's JwtBearerHandler
/// hands <c>options.ConfigurationManager</c> straight into
/// <c>TokenValidationParameters.ConfigurationManager</c> and lets Microsoft.IdentityModel.Tokens's
/// own validation code call <c>GetBaseConfigurationAsync</c> itself -- JwtBearerHandler never
/// calls it directly. A genuine metadata-fetch failure therefore never surfaces to
/// <c>OnAuthenticationFailed</c> as a raw HttpRequestException/IOException/JsonException/
/// OperationCanceledException the way the existing unit tests (which invoke
/// <c>OnAuthenticationFailed</c> directly with a fake exception) assume -- it either never
/// surfaces at all (a warm cache's background refresh failure is swallowed internally by
/// <c>ConfigurationManager&lt;T&gt;</c>) or surfaces, mid-validation, as an unrelated
/// <c>SecurityTokenSignatureKeyNotFoundException</c>. Interposing directly on
/// <see cref="GetBaseConfigurationAsync"/> -- the one call every code path, cold or warm, that
/// could ever actually fetch over the network funnels through -- sees every genuine fetch outcome
/// regardless of which exception shape or downstream code path follows it.
///
/// Only a cold-start fetch (no configuration ever held) can throw back to an awaiting caller, by
/// construction: once warm, <c>ConfigurationManager&lt;T&gt;.GetConfigurationAsync</c> either
/// returns the held configuration immediately with no network call at all (still within
/// <c>AutomaticRefreshInterval</c>, 12h default), or kicks off a detached, fire-and-forget
/// background refresh and STILL returns the held configuration immediately -- a background
/// refresh's failure is logged internally and never propagates to the awaiting caller. So the
/// catch block below is unreachable for any warm call, by construction of the wrapped type, not
/// by any extra "is this call a background refresh" detection logic here.
///
/// This also means a forged/unknown-kid token can never arm the gate: resolving the
/// configuration (this method) succeeds whenever ANY configuration is held (warm, or a freshly
/// successful cold fetch); an unknown kid only ever fails the SEPARATE, later signing-key lookup
/// against that already-successfully-returned configuration
/// (<c>SecurityTokenSignatureKeyNotFoundException</c>), entirely outside this method's try/catch.
/// <c>EntraAuthentication.ConfigureJwtBearer</c> also sets <c>RefreshOnIssuerKeyNotFound = false</c>
/// precisely because of that same exception: <c>JwtBearerHandler.RecordTokenValidationError</c>
/// would otherwise call <see cref="RequestRefresh"/> on it, and
/// <c>ConfigurationManager&lt;T&gt;.RequestRefresh</c>'s <c>RefreshInterval</c> throttle (5-minute
/// default) does NOT apply to the very first call in the process's lifetime -- an unthrottled,
/// detached background re-fetch of a cold/hung IdP, entirely outside this decorator's/
/// <see cref="DiscoveryFailureGate"/>'s view. See <c>ConfigureJwtBearer</c>'s own remarks for the
/// full story.
///
/// Setting <see cref="BaseConfigurationManager.UseLastKnownGoodConfiguration"/> /
/// <see cref="BaseConfigurationManager.LastKnownGoodLifetime"/> directly on an instance of this
/// decorator (which is itself a <see cref="BaseConfigurationManager"/>) satisfies #226 Rick's
/// LOW-MED finding (fix #3) "for free": Microsoft.IdentityModel.Tokens's internal validation reads
/// and writes <c>LastKnownGoodConfiguration</c> on whatever <c>BaseConfigurationManager</c>
/// instance is assigned to <c>TokenValidationParameters.ConfigurationManager</c> -- i.e. THIS
/// decorator, once wired in as <c>options.ConfigurationManager</c> -- never the wrapped inner
/// manager. Both properties are concrete, non-virtual members inherited as-is from
/// <see cref="BaseConfigurationManager"/>; no extra plumbing is needed here beyond setting them
/// once at construction time in EntraAuthentication.ConfigureJwtBearer.
/// </summary>
public sealed class CooldownAwareConfigurationManager :
    BaseConfigurationManager, IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _inner;
    private readonly DiscoveryFailureGate _gate;
    private volatile bool _hasConfiguration;

    public CooldownAwareConfigurationManager(ConfigurationManager<OpenIdConnectConfiguration> inner, DiscoveryFailureGate gate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>
    /// True once this decorator has observed at least one successfully-returned configuration.
    /// This is the signal EntraAuthentication.ConfigureJwtBearer's OnMessageReceived short-circuit
    /// now reads (replacing the former, less precise
    /// <see cref="BaseConfigurationManager.IsLastKnownGoodValid"/> check): "cached" must mean "a
    /// configuration is actually held", not "a Last-Known-Good snapshot happens to still be within
    /// its own, separate expiry window" -- fix #3's deliberately-shortened
    /// <see cref="BaseConfigurationManager.LastKnownGoodLifetime"/> would otherwise make
    /// <c>IsLastKnownGoodValid</c> flicker false long before the held, perfectly-usable current
    /// configuration itself has gone stale, re-arming the "block requests with a cached, resolvable
    /// kid" bug fix #1 exists to close.
    /// </summary>
    public bool HasConfiguration => _hasConfiguration;

    public override async Task<BaseConfiguration> GetBaseConfigurationAsync(CancellationToken cancel)
    {
        try
        {
            var configuration = await _inner.GetBaseConfigurationAsync(cancel).ConfigureAwait(false);
            _hasConfiguration = true;
            return configuration;
        }
        catch (Exception ex) when (EntraAuthentication.IsDiscoveryOrBackchannelFailure(ex))
        {
            // Only record a failure when no configuration is held at all -- deciding "cached" by
            // _hasConfiguration (not IsLastKnownGoodValid) is exactly what stops a forged-kid
            // token (which never reaches this catch block in the first place, see class remarks)
            // or a warm-cache background-refresh failure (which never throws here either) from
            // ever arming the cooldown while a usable configuration is actually held.
            if (!_hasConfiguration)
            {
                _gate.RecordFailure();
            }
            throw;
        }
    }

    /// <summary>
    /// <see cref="IConfigurationManager{T}"/>'s typed entrypoint -- not actually reachable on the
    /// real pipeline (JwtBearerHandler.SetupTokenValidationParametersAsync only ever calls this
    /// when <c>Options.ConfigurationManager</c> is NOT a <see cref="BaseConfigurationManager"/>,
    /// which this decorator always is), but implemented here (rather than left to throw
    /// NotImplementedException) so it still behaves correctly -- including arming the cooldown
    /// gate on a genuine fetch failure -- for any caller (test or otherwise) that invokes it
    /// directly. Routed through <see cref="GetBaseConfigurationAsync"/> rather than calling
    /// <c>_inner.GetConfigurationAsync</c> directly so both entrypoints share one code path.
    /// </summary>
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
        (OpenIdConnectConfiguration)await GetBaseConfigurationAsync(cancel).ConfigureAwait(false);

    /// <summary>
    /// Plain passthrough -- <c>RequestRefresh</c> can never itself fail or throw (it only ever
    /// mutates internal scheduling state and optionally spawns an unawaited background task), so
    /// there is nothing for this decorator to observe or gate here.
    /// </summary>
    public override void RequestRefresh() => _inner.RequestRefresh();
}

using System.Collections.Concurrent;

namespace Conformance.Harness;

/// <summary>
/// Issue #143 (ADR-002 conformance harness): the ambient registry behind "the harness HTTP client
/// and RealtimeBrowserClient attach a valid token by default". Keyed by a backend's own
/// <c>BaseUri</c> authority (host:port) -- unique per fixture instance, since
/// <see cref="NetworkUtils.GetFreeTcpPort"/> allocates a fresh random port per backend launch --
/// so there is no cross-collection contamination risk between xunit collections running fixtures
/// in parallel, and no call-site changes are needed anywhere: <see cref="ConformanceHttpClient"/>
/// and <see cref="RealtimeBrowserClient"/> both consult this registry only when a caller didn't
/// explicitly pass its own token.
///
/// An Entra-mode <see cref="ConformanceFixture"/> registers a minting delegate here right after
/// its backend starts (<c>InitializeAsync</c>) and unregisters it in <c>DisposeAsync</c>. A
/// <see cref="FakeEntraIssuer"/>-backed delegate mints a *fresh* token on every call (rather than
/// caching one string) so a long-running collection never risks a token's `exp`/`nbf` window
/// going stale relative to "now" partway through a slow test run.
/// </summary>
public static class EntraDefaultCredentials
{
    private static readonly ConcurrentDictionary<string, Func<string>> MintersByAuthority = new(StringComparer.Ordinal);

    /// <summary>Registers <paramref name="mintAccessToken"/> as the default access-token source for
    /// requests/connections whose target URI authority matches <paramref name="backendBaseUri"/>'s.
    /// Overwrites any previous registration for the same authority (a fixture re-registering after
    /// a restart, say).</summary>
    public static void Register(Uri backendBaseUri, Func<string> mintAccessToken) =>
        MintersByAuthority[backendBaseUri.Authority] = mintAccessToken;

    /// <summary>Removes whatever was registered for <paramref name="backendBaseUri"/>'s authority,
    /// if anything -- called from a fixture's teardown so a disposed backend's port can never be
    /// mistaken for still having a live default token once some other fixture reuses that port
    /// number by coincidence.</summary>
    public static void Unregister(Uri backendBaseUri) => MintersByAuthority.TryRemove(backendBaseUri.Authority, out _);

    /// <summary>Mints and returns a fresh default access token for <paramref name="requestUri"/>'s
    /// authority, or null if nothing is registered for it (a <see cref="DevelopmentPassThroughFixture"/>'s
    /// backend, an unrelated host, or a fixture that hasn't started yet).</summary>
    public static string? TryGetAccessToken(Uri requestUri) =>
        MintersByAuthority.TryGetValue(requestUri.Authority, out var mint) ? mint() : null;
}

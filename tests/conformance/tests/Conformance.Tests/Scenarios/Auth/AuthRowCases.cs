using Conformance.Fakes;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>What a token-shape row (18.11 rows 1 to 8, including 6b) should produce.</summary>
public enum AuthRowExpectedOutcome
{
    /// <summary>401, with <c>WWW-Authenticate: Bearer</c> on the response.</summary>
    Reject401,

    /// <summary>403 (a structurally valid, correctly-signed token missing the role or scope).</summary>
    Reject403,

    /// <summary>200 on REST; the socket opens (and <c>extension.metadata</c> arrives) on <c>/realtime</c>.</summary>
    Accept,
}

/// <summary>
/// One persona-architecture.md 18.11 token-shape row (1 to 8, including 6b) -- shared between the
/// REST and <c>/realtime</c> variants of each row, since (per 18.11's own header) "Each runs on
/// REST ... and on <c>/realtime</c> unless noted", and every one of rows 1-8 is exactly that: no
/// exception noted. Rows 9 to 16 have their own per-row shape (REST-only, realtime-only, or not a
/// per-route assertion at all) and are covered by the other files in this folder instead of here.
/// </summary>
public sealed record AuthRowTokenCase(
    string Row, string Description, Func<FakeEntraIssuer, string?> MintToken, AuthRowExpectedOutcome Expected)
{
    /// <summary>
    /// Whether the realtime variant should also attach a session <c>?token=</c> alongside
    /// whatever <see cref="MintToken"/> produces for <c>?access_token=</c>. True for every row
    /// except row 1: 18.11 keeps "no token at all" (row 1) and "the realtime session token
    /// missing/bound to another oid" (row 10, a *valid* access token with a bad/missing session
    /// token) as two deliberately distinct cases -- row 1 must omit both query params, not just
    /// the access token, to stay the true "nothing at all" baseline row 10 is contrasted against.
    /// </summary>
    public bool RealtimeAttachSessionToken { get; init; } = true;

    /// <summary>A second, unrelated tenant GUID -- row 2's "wrong tenant" only needs any tenant
    /// other than <see cref="FakeEntraIssuer.DefaultTenantId"/> (the one ENTRA_TENANT_ID is
    /// configured to); it doesn't need to itself be a real, dialable tenant.</summary>
    public const string OtherTenantId = "55555555-5555-5555-5555-555555555555";

    /// <summary>A second, unrelated client/audience GUID -- row 3's "wrong audience" only needs
    /// any audience other than <see cref="FakeEntraIssuer.DefaultClientId"/>.</summary>
    public const string OtherAudience = "66666666-6666-6666-6666-666666666666";

    /// <summary>
    /// Every row this file covers, in 18.11's own order. Row 1 ("no token") has a null
    /// <see cref="MintToken"/> result -- callers must omit the Authorization header/access_token
    /// entirely, not send an empty one, matching <see cref="RealtimeBrowserClient"/>'s own
    /// attachAccessToken=false contract (Conformance.Harness).
    /// </summary>
    public static readonly AuthRowTokenCase[] All =
    [
        new("1", "No token", _ => null, AuthRowExpectedOutcome.Reject401) { RealtimeAttachSessionToken = false },

        new("2", "Wrong tenant (issuer and tid of another tenant, signed with the published key)",
            issuer => issuer.Mint(new FakeEntraTokenOverrides
            {
                Tenant = OtherTenantId,
                Issuer = issuer.IssuerFor(OtherTenantId),
            }),
            AuthRowExpectedOutcome.Reject401),

        new("3", "Wrong audience",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Audience = OtherAudience }),
            AuthRowExpectedOutcome.Reject401),

        new("4", "Missing role",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Roles = [] }),
            AuthRowExpectedOutcome.Reject403),

        new("4b", "Wrong role (present, but not the required one)",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Roles = ["Some.Other.Role"] }),
            AuthRowExpectedOutcome.Reject403),

        new("4c", "Substring-guard role (a role that merely starts with the required one)",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Roles = ["DriveThru.UserX"] }),
            AuthRowExpectedOutcome.Reject403),

        new("5", "Missing scope; app-only token (roles, no scp)",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { OmitScope = true }),
            AuthRowExpectedOutcome.Reject403),

        new("5b", "Wrong scope (present, but not the required one)",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Scope = "User.Read" }),
            AuthRowExpectedOutcome.Reject403),

        new("5c", "Substring-guard scope (a scope that merely starts with the required one)",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Scope = "access_as_user_admin" }),
            AuthRowExpectedOutcome.Reject403),

        new("6", "Expired past the skew (exp = now minus 10 min)",
            issuer => issuer.Mint(new FakeEntraTokenOverrides
            {
                Exp = DateTimeOffset.UtcNow.AddMinutes(-10),
                Nbf = DateTimeOffset.UtcNow.AddMinutes(-20),
            }),
            AuthRowExpectedOutcome.Reject401),

        new("6b-inside", "Expired inside the 5-minute skew (exp = now minus 4m30s) -- pins the skew's inside edge",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Exp = DateTimeOffset.UtcNow.AddMinutes(-4).AddSeconds(-30) }),
            AuthRowExpectedOutcome.Accept),

        new("6b-outside", "Expired past the 5-minute skew (exp = now minus 5m30s) -- pins the skew's outside edge",
            issuer => issuer.Mint(new FakeEntraTokenOverrides
            {
                Exp = DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(-30),
                Nbf = DateTimeOffset.UtcNow.AddMinutes(-20),
            }),
            AuthRowExpectedOutcome.Reject401),

        new("6c-inside", "Not yet valid inside the 5-minute skew (nbf = now plus 4m30s) -- pins the skew's inside edge on nbf",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Nbf = DateTimeOffset.UtcNow.AddMinutes(4).AddSeconds(30) }),
            AuthRowExpectedOutcome.Accept),

        new("6c-outside", "Not yet valid past the 5-minute skew (nbf = now plus 5m30s) -- pins the skew's outside edge on nbf",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Nbf = DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(30) }),
            AuthRowExpectedOutcome.Reject401),

        new("7 (bad signature)", "Bad signature (unpublished key)",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Key = FakeEntraSigningKey.Unpublished }),
            AuthRowExpectedOutcome.Reject401),

        new("7 (alg: none)", "alg: none",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Alg = "none" }),
            AuthRowExpectedOutcome.Reject401),

        new("7 (HS256)", "HS256",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Alg = "HS256" }),
            AuthRowExpectedOutcome.Reject401),

        new("7 (bad signature, published kid)", "Bad signature (unpublished key, but the header claims the published kid)",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Key = FakeEntraSigningKey.Unpublished, Kid = FakeEntraIssuer.PublishedKid }),
            AuthRowExpectedOutcome.Reject401),

        new("7 (alg: none, published kid)", "alg: none, but the header claims the published kid",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Alg = "none", Kid = FakeEntraIssuer.PublishedKid }),
            AuthRowExpectedOutcome.Reject401),

        new("7 (HS256, published kid)", "HS256, but the header claims the published kid",
            issuer => issuer.Mint(new FakeEntraTokenOverrides { Alg = "HS256", Kid = FakeEntraIssuer.PublishedKid }),
            AuthRowExpectedOutcome.Reject401),

        new("8", "Valid", issuer => issuer.Mint(), AuthRowExpectedOutcome.Accept),
    ];

    public override string ToString() => $"Row {Row}: {Description}";
}

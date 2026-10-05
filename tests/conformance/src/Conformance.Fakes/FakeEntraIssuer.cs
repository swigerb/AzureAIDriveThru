using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

namespace Conformance.Fakes;

/// <summary>Which signing key <see cref="FakeEntraIssuer.Mint"/> should use. <see cref="Unpublished"/>
/// signs with a key that is never served from the JWKS endpoint -- 18.11 row 7's bad-signature
/// case: a validator that actually fetches the JWKS (rather than trusting whatever `kid` a token
/// claims) can never find a matching key for it.</summary>
public enum FakeEntraSigningKey
{
    Published,
    Unpublished,
}

/// <summary>Every claim/header <see cref="FakeEntraIssuer.Mint"/> lets a caller override, matching
/// issue #143's exact list (iss, tid, aud, roles, scp, oid, exp, nbf, alg, key). Anything left null
/// gets a sensible "valid token" default -- see <see cref="FakeEntraIssuer.Mint"/> for the exact
/// defaults, and persona-architecture.md 18.11 for which override each row needs.</summary>
public sealed class FakeEntraTokenOverrides
{
    /// <summary>Default: this issuer's own <see cref="FakeEntraIssuer.IssuerFor"/> for <see cref="Tenant"/>.</summary>
    public string? Issuer { get; init; }

    /// <summary>The `tid` claim, and (unless <see cref="Issuer"/> is also set) what the issuer URL's
    /// tenant segment is computed from. Row 2 (wrong tenant) only needs to set this.</summary>
    public string? Tenant { get; init; }

    /// <summary>Default: <see cref="FakeEntraIssuer.DefaultClientId"/>.</summary>
    public string? Audience { get; init; }

    /// <summary>Default: <c>[FakeEntraIssuer.DefaultRole]</c>. Pass an empty list for row 4
    /// (missing role).</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>Default: <see cref="FakeEntraIssuer.DefaultScope"/>. Ignored entirely when
    /// <see cref="OmitScope"/> is set.</summary>
    public string? Scope { get; init; }

    /// <summary>Whether to omit the `scp` claim entirely, distinct from an empty-string scope --
    /// row 5's "app-only token (roles, no scp)" needs the claim itself absent, not merely empty.</summary>
    public bool OmitScope { get; init; }

    /// <summary>Default: <see cref="FakeEntraIssuer.DefaultOid"/>.</summary>
    public string? Oid { get; init; }

    /// <summary>Default: now + 15 minutes.</summary>
    public DateTimeOffset? Exp { get; init; }

    /// <summary>Default: now - 5 minutes (already-usable, matching a token minted moments after sign-in).</summary>
    public DateTimeOffset? Nbf { get; init; }

    /// <summary>#226 Rick's review, fix #2 (MEDIUM): omit the `nbf` claim entirely instead of the
    /// usual "already valid" default -- entra_auth.py's `jwt.decode(..., options={"require":
    /// ["exp", "nbf"]})` 401s when `nbf` is absent, and ASP.NET Core's JwtBearer stack has no
    /// equivalent "require nbf" built-in (IdentityModel's lifetime validation silently treats an
    /// absent nbf as "no lower bound to check"), so this needs its own conformance row. Takes
    /// priority over <see cref="Nbf"/> when both are set.</summary>
    public bool OmitNbf { get; init; }

    /// <summary>#226 Rick's review, fix #2 (MEDIUM): with exactly one role in <see cref="Roles"/>,
    /// skip the usual array-shape coercion (see <see cref="FakeEntraIssuer.Mint"/>'s R4 remarks)
    /// so <c>System.IdentityModel.Tokens.Jwt</c>'s own default claim aggregation collapses the
    /// single `roles` claim to a bare JSON string instead of a one-element array -- a malformed
    /// shape real Entra never actually produces, but one a validator must still guard against
    /// (entra_auth.py's `isinstance(roles, list)` 403s on it; ASP.NET Core's single materialized
    /// Claim can't tell a genuine one-element array from a bare value, so this must be checked
    /// against the raw JSON payload -- see EntraAuthentication.OnTokenValidated).</summary>
    public bool MalformedRolesShape { get; init; }

    /// <summary>#226 Rick's review, fix #2 (MEDIUM): force the `scp` claim to serialize as a
    /// one-element JSON array instead of its normal space-delimited string -- a malformed shape
    /// real Entra never actually produces, but one a validator must still guard against
    /// (entra_auth.py's `scp.split() if isinstance(scp, str) else []` 403s on it the same way a
    /// missing `scp` does). Ignored when <see cref="OmitScope"/> is also set.</summary>
    public bool MalformedScopeShape { get; init; }

    /// <summary>"RS256" (default), "HS256", or "none" -- row 7's three bad-signature variants.
    /// "none" signs with no credentials at all (an unsigned JWS), which <see
    /// cref="JwtSecurityTokenHandler"/> writes correctly with no extra code. "HS256" signs with a
    /// fixed, never-published shared secret -- wrong algorithm family entirely, so it can never
    /// validate against the RS256-only JWKS regardless of `kid`.</summary>
    public string? Alg { get; init; }

    /// <summary>Default: <see cref="FakeEntraSigningKey.Published"/>.</summary>
    public FakeEntraSigningKey Key { get; init; } = FakeEntraSigningKey.Published;

    /// <summary>Overrides the `kid` header claim without changing which key actually signs --
    /// exercises a validator's own kid-lookup-miss handling distinctly from a genuinely-unpublished
    /// key. Null (default) uses the real kid for <see cref="Key"/>.</summary>
    public string? Kid { get; init; }
}

/// <summary>
/// Issue #143 (ADR-002 conformance harness): a Kestrel-hosted fake of the two Entra endpoints the
/// backends' token validation needs -- the OIDC discovery document and the JWKS -- following the
/// same hosting pattern as <see cref="FakeSearchServer"/>/<see cref="FakeChatCompletionsServer"/>
/// (WebApplication, BaseUri, StartAsync(fixedPort:)).
///
/// Serves:
/// - `GET /{tenant}/v2.0/.well-known/openid-configuration`, issuer `http://127.0.0.1:{port}/{tenant}/v2.0`,
///   `jwks_uri` pointing at the JWKS route below. The `{tenant}` route segment is a wildcard --
///   this fake answers for any tenant path, exactly like the real multi-tenant discovery document
///   family, but a backend only ever fetches the ONE tenant it was configured with
///   (ENTRA_TENANT_ID); a "wrong tenant" minted token (18.11 row 2) fails on issuer/tid mismatch
///   against whatever that configured tenant's discovery said, never because this fake refused to
///   answer for the other tenant.
/// - `GET /{tenant}/discovery/v2.0/keys`, the JWKS -- one published RSA key (what real validators
///   actually use) and nothing else; a second RSA key is generated and used by <see cref="Mint"/>
///   for row 7's bad-signature case but is deliberately never exposed here.
///
/// <see cref="Mint"/> mints a signed JWT for any of the two keys, matching persona-architecture.md
/// 18.11's row table claim-by-claim.
/// </summary>
public sealed class FakeEntraIssuer : IAsyncDisposable
{
    /// <summary>Well-known, fixed test GUID -- not a real Entra tenant. Every Entra-mode fixture
    /// sets ENTRA_TENANT_ID to this so #144/#147's own validation config lines up with what
    /// <see cref="Mint"/> defaults to.</summary>
    public const string DefaultTenantId = "11111111-1111-1111-1111-111111111111";

    /// <summary>Well-known, fixed test GUID -- not a real Entra app registration. Every Entra-mode
    /// fixture sets ENTRA_CLIENT_ID to this.</summary>
    public const string DefaultClientId = "22222222-2222-2222-2222-222222222222";

    /// <summary>Well-known, fixed test GUID standing in for a signed-in user's `oid`.</summary>
    public const string DefaultOid = "33333333-3333-3333-3333-333333333333";

    /// <summary>A second well-known test GUID -- row 10's "session token minted for another oid".</summary>
    public const string OtherOid = "44444444-4444-4444-4444-444444444444";

    /// <summary>Matches persona-architecture.md 18.5's ENTRA_APP_ROLE default.</summary>
    public const string DefaultRole = "DriveThru.User";

    /// <summary>Matches persona-architecture.md 18.5's ENTRA_API_SCOPE default.</summary>
    public const string DefaultScope = "access_as_user";

    /// <summary>The `kid` header claim of the one key this issuer's JWKS actually serves -- public
    /// so tests (18.11 row 7's published-kid bad-signature/none/HS256 variants) can mint a token
    /// that carries this real, resolvable kid while still failing on signature or alg, proving a
    /// validator's alg/signature check runs rather than short-circuiting on a key-lookup miss.</summary>
    public const string PublishedKid = "conformance-fake-entra-published-key";
    private const string UnpublishedKid = "conformance-fake-entra-unpublished-key";

    // Deliberately fixed, not random: row 7's HS256 sub-case only needs "some algorithm the
    // RS256-only JWKS can never satisfy", not a secret worth protecting -- this is a fake test
    // issuer, never reachable outside this machine's loopback interface.
    private static readonly byte[] Hs256Secret =
        Encoding.UTF8.GetBytes("conformance-fake-entra-issuer-hs256-secret-never-published-not-a-real-secret");

    private WebApplication? _app;
    private RSA? _publishedKey;
    private RSA? _unpublishedKey;

    /// <summary>The default tenant this instance's discovery/JWKS/Mint defaults resolve against --
    /// always <see cref="DefaultTenantId"/> today. Kept as an instance member (not just the
    /// constant) so a future test that needs a *second*, independently-tenanted fake issuer isn't
    /// blocked on this type assuming there's only ever one.</summary>
    public string Tenant { get; }

    /// <summary>Set once <see cref="StartAsync"/> returns -- `http://127.0.0.1:{port}/`, matching
    /// ENTRA_INSTANCE's exact shape (trailing slash) in persona-architecture.md 18.5/18.11.</summary>
    public Uri BaseUri { get; private set; } = new("http://127.0.0.1:0/");

    /// <summary>This instance's own issuer string for <see cref="Tenant"/> -- `Mint`'s default `iss`.</summary>
    public string Issuer => IssuerFor(Tenant);

    public FakeEntraIssuer(string? tenant = null)
    {
        Tenant = tenant ?? DefaultTenantId;
    }

    /// <summary>The issuer URL for an arbitrary tenant segment -- what the discovery document for
    /// that `{tenant}` route would report, and what a "wrong tenant" minted token
    /// (<see cref="FakeEntraTokenOverrides.Tenant"/>) should set as its own `iss` by default.</summary>
    public string IssuerFor(string tenant) => new Uri(BaseUri, $"{tenant}/v2.0").ToString();

    /// <summary>The JWKS URL for an arbitrary tenant segment -- what that tenant's discovery
    /// document reports as `jwks_uri`. One shared JWKS regardless of tenant, matching a single
    /// app registration's key material being tenant-independent.</summary>
    public string JwksUriFor(string tenant) => new Uri(BaseUri, $"{tenant}/discovery/v2.0/keys").ToString();

    public async Task StartAsync(CancellationToken cancellationToken = default, int? fixedPort = null)
    {
        _publishedKey = RSA.Create(2048);
        _unpublishedKey = RSA.Create(2048);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{fixedPort?.ToString() ?? "0"}");
        var app = builder.Build();

        app.MapGet("/{tenant}/v2.0/.well-known/openid-configuration", (string tenant) =>
        {
            var issuer = IssuerFor(tenant);
            return Results.Json(new
            {
                issuer,
                jwks_uri = JwksUriFor(tenant),
                authorization_endpoint = $"{issuer}/oauth2/v2.0/authorize",
                token_endpoint = $"{issuer}/oauth2/v2.0/token",
                response_types_supported = new[] { "code", "id_token", "code id_token", "id_token token" },
                response_modes_supported = new[] { "query", "fragment", "form_post" },
                subject_types_supported = new[] { "pairwise" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
            });
        });

        app.MapGet("/{tenant}/discovery/v2.0/keys", () => Results.Json(BuildJwks()));

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        BaseUri = new Uri(app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        _publishedKey?.Dispose();
        _unpublishedKey?.Dispose();
    }

    private object BuildJwks()
    {
        var publishedJwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_publishedKey!.ExportParameters(false))
        {
            KeyId = PublishedKid,
        });
        publishedJwk.KeyId = PublishedKid;
        publishedJwk.Use = "sig";
        publishedJwk.Alg = "RS256";
        return new
        {
            keys = new[]
            {
                new
                {
                    kty = publishedJwk.Kty,
                    use = publishedJwk.Use,
                    kid = publishedJwk.KeyId,
                    n = publishedJwk.N,
                    e = publishedJwk.E,
                    alg = publishedJwk.Alg,
                },
            },
        };
    }

    /// <summary>
    /// Mints a signed JWT with the given overrides layered onto a "valid Entra token" default
    /// (this issuer's own <see cref="Issuer"/>/<see cref="Tenant"/>, <see cref="DefaultClientId"/>
    /// audience, <see cref="DefaultRole"/> role, <see cref="DefaultScope"/> scope,
    /// <see cref="DefaultOid"/>, a 15-minute lifetime already valid, RS256 signed with the
    /// published key) -- exactly matching persona-architecture.md 18.11 row 8. Every other row
    /// only needs to override the one or two fields that make it that row.
    /// </summary>
    public string Mint(FakeEntraTokenOverrides? overrides = null)
    {
        overrides ??= new FakeEntraTokenOverrides();

        var tenant = overrides.Tenant ?? Tenant;
        var issuer = overrides.Issuer ?? IssuerFor(tenant);
        var audience = overrides.Audience ?? DefaultClientId;
        var now = DateTimeOffset.UtcNow;
        var exp = overrides.Exp ?? now.AddMinutes(15);
        var nbf = overrides.Nbf ?? now.AddMinutes(-5);
        var roles = overrides.Roles ?? new[] { DefaultRole };

        var claims = new List<Claim>
        {
            new("tid", tenant),
            new("oid", overrides.Oid ?? DefaultOid),
            new("name", "Conformance Test User"),
            new("preferred_username", "conformance-test-user@example.test"),
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
        }
        if (!overrides.OmitScope)
        {
            var scope = overrides.Scope ?? DefaultScope;
            claims.Add(new Claim("scp", scope));
        }

        var alg = overrides.Alg ?? "RS256";
        var signingCredentials = alg switch
        {
            "RS256" => BuildRsaSigningCredentials(overrides.Key, overrides.Kid),
            "HS256" => new SigningCredentials(new SymmetricSecurityKey(Hs256Secret), SecurityAlgorithms.HmacSha256),
            "none" => null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(overrides), alg, "FakeEntraTokenOverrides.Alg must be \"RS256\", \"HS256\", or \"none\"."),
        };

        // #226 Rick's review, fix #2 (MEDIUM): OmitNbf needs the claim absent entirely, not
        // merely a value -- JwtSecurityToken's notBefore parameter is nullable precisely for this
        // (a null value skips adding the "nbf" payload claim at all), unlike every other row which
        // always wants a concrete nbf.
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: overrides.OmitNbf ? null : nbf.UtcDateTime,
            expires: exp.UtcDateTime,
            signingCredentials: signingCredentials);

        // R4 (Rick's PR #158 round 1 review): JwtPayload's own claim-aggregation collapses a
        // SINGLE claim of a given Type to a bare JSON string, not a 1-element array -- but real
        // Entra always emits `roles` as a JSON array, even for one role. Force array
        // serialization explicitly; roles.Count == 0 already leaves the key absent entirely (the
        // foreach above never added a "roles" claim in that case), matching real Entra's own
        // "omit the claim when no app roles are assigned" behaviour.
        //
        // #226 Rick's review, fix #2 (MEDIUM): MalformedRolesShape deliberately skips this
        // coercion -- letting the single-claim collapse through unforced reproduces the exact
        // malformed ("roles" as a bare string, not an array) shape a validator must still 403 on.
        if (roles.Count > 0 && !overrides.MalformedRolesShape)
        {
            token.Payload["roles"] = roles.ToArray();
        }

        // #226 Rick's review, fix #2 (MEDIUM): MalformedScopeShape forces `scp` into a one-element
        // JSON array -- a shape real Entra never produces (scp is always a single
        // space-delimited string claim), but one entra_auth.py's `isinstance(scp, str)` guard
        // (and this port's own JsonWebToken-payload scp-shape check) must still 403 on.
        if (!overrides.OmitScope && overrides.MalformedScopeShape)
        {
            token.Payload["scp"] = new[] { overrides.Scope ?? DefaultScope };
        }

        // R5 (Rick's PR #158 round 1 review): only the RS256 path ever wrote a `kid` header claim
        // (BuildRsaSigningCredentials always sets one); "none" and HS256 wrote no `kid` at all, so
        // a validator that looks the signing key up by `kid` (PyJWKClient, JwtBearer) never even
        // reaches the alg/signature check for those two shapes. Apply FakeEntraTokenOverrides.Kid
        // uniformly across all three algs -- 18.11 row 7's published-kid variants need a
        // none/HS256 token that carries the real published kid, so the alg/signature check is
        // what actually rejects it, not a key-lookup miss.
        if (alg != "RS256" && overrides.Kid is not null)
        {
            token.Header["kid"] = overrides.Kid;
        }

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private SigningCredentials BuildRsaSigningCredentials(FakeEntraSigningKey key, string? kidOverride)
    {
        var (rsa, kid) = key switch
        {
            FakeEntraSigningKey.Published => (_publishedKey!, PublishedKid),
            FakeEntraSigningKey.Unpublished => (_unpublishedKey!, UnpublishedKid),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };
        var securityKey = new RsaSecurityKey(rsa) { KeyId = kidOverride ?? kid };
        return new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256);
    }
}

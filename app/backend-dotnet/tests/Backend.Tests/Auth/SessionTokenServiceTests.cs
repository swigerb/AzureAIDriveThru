using Backend.Auth;

namespace Backend.Tests.Auth;

/// <summary>Round-trip tests for SessionTokenService, the byte-for-byte port of rtmt.py's
/// create_hmac_token/validate_hmac_token (spike #44's cross-backend interoperability concern --
/// see docs/dotnet_mapping.md and the #44 comment for the position this suite backs up).</summary>
public sealed class SessionTokenServiceTests
{
    private static readonly byte[] Secret = "test-secret-at-least-32-bytes-long!"u8.ToArray();

    [Fact]
    public void CreateThenValidate_RoundTrips()
    {
        var service = new SessionTokenService(Secret);

        var token = service.Create();

        Assert.True(service.Validate(token));
    }

    [Fact]
    public void Validate_RejectsExpiredToken()
    {
        var service = new SessionTokenService(Secret);

        var token = service.Create(expirySeconds: -10);

        Assert.False(service.Validate(token));
    }

    [Fact]
    public void Validate_RejectsTamperedPayload()
    {
        var service = new SessionTokenService(Secret);
        var token = service.Create();
        var lastDot = token.LastIndexOf('.');
        var tampered = token[..lastDot] + "AAAA" + token[lastDot..];

        Assert.False(service.Validate(tampered));
    }

    [Fact]
    public void Validate_RejectsTamperedSignature()
    {
        var service = new SessionTokenService(Secret);
        var token = service.Create();
        var lastDot = token.LastIndexOf('.');
        var tampered = token[..(lastDot + 1)] + new string('0', token.Length - lastDot - 1);

        Assert.False(service.Validate(tampered));
    }

    [Fact]
    public void Validate_RejectsTokenSignedWithDifferentSecret()
    {
        var issuer = new SessionTokenService(Secret);
        var verifier = new SessionTokenService("a-completely-different-secret!!"u8.ToArray());

        var token = issuer.Create();

        Assert.False(verifier.Validate(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token-at-all")]
    [InlineData(".")]
    public void Validate_RejectsMalformedTokens(string malformed)
    {
        var service = new SessionTokenService(Secret);

        Assert.False(service.Validate(malformed));
    }

    [Fact]
    public void Create_PayloadMatchesPythonJsonDumpsSpacing()
    {
        // Byte-compatibility guard for #44: Python's json.dumps({"exp": n}) inserts exactly one
        // space after the colon and no other whitespace. Decode our own base64url payload and
        // assert the exact string shape rather than just round-tripping through our own Validate.
        var service = new SessionTokenService(Secret);

        var token = service.Create(expirySeconds: 900);
        var payloadB64 = token[..token.LastIndexOf('.')];
        var payloadJson = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(payloadB64.Replace('-', '+').Replace('_', '/')));

        Assert.Matches(@"^\{""exp"": \d+\}$", payloadJson);
    }

    [Fact]
    public void Validate_RejectsUppercaseSignature()
    {
        // PR #96 review nit: Python's hmac.compare_digest compares the presented signature
        // byte-for-byte against hexdigest()'s always-lowercase output, so an otherwise-correct
        // signature re-cased to uppercase must be rejected, not silently accepted. A previous
        // draft called .ToLowerInvariant() on the presented signature before comparing, which
        // broke this parity.
        var service = new SessionTokenService(Secret);
        var token = service.Create();
        var lastDot = token.LastIndexOf('.');
        var uppercased = token[..(lastDot + 1)] + token[(lastDot + 1)..].ToUpperInvariant();

        Assert.False(service.Validate(uppercased));
    }

    // ── Issue #147 (ADR-002, design doc 18.3): the layered session token's `oid` binding ─────────

    [Fact]
    public void TryValidate_RoundTripsOid()
    {
        var service = new SessionTokenService(Secret);
        var token = service.Create(expirySeconds: 900, oid: "33333333-3333-3333-3333-333333333333");

        Assert.True(service.TryValidate(token, out var oid));
        Assert.Equal("33333333-3333-3333-3333-333333333333", oid);
    }

    [Fact]
    public void TryValidate_OmittedOid_ReturnsNull()
    {
        var service = new SessionTokenService(Secret);
        var token = service.Create(expirySeconds: 900);

        Assert.True(service.TryValidate(token, out var oid));
        Assert.Null(oid);
    }

    [Fact]
    public void TryValidate_ExpiredTokenWithOid_ReturnsNullOid()
    {
        var service = new SessionTokenService(Secret);
        var token = service.Create(expirySeconds: -10, oid: "33333333-3333-3333-3333-333333333333");

        Assert.False(service.TryValidate(token, out var oid));
        Assert.Null(oid);
    }

    [Fact]
    public void Create_WithOid_PayloadMatchesPythonJsonDumpsSpacing()
    {
        // Same byte-compatibility guard as the exp-only case above, but for create_hmac_token's
        // `oid` branch: Python's json.dumps({"exp": n, "oid": oid}) inserts exp before oid, one
        // space after each colon, one space after the comma, no trailing whitespace.
        var service = new SessionTokenService(Secret);

        var token = service.Create(expirySeconds: 900, oid: "33333333-3333-3333-3333-333333333333");
        var payloadB64 = token[..token.LastIndexOf('.')];
        var payloadJson = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(payloadB64.Replace('-', '+').Replace('_', '/')));

        Assert.Matches(
            @"^\{""exp"": \d+, ""oid"": ""33333333-3333-3333-3333-333333333333""\}$", payloadJson);
    }

    [Fact]
    public void TryValidate_TamperedOid_FailsSignatureCheck()
    {
        // The oid is inside the signed payload, not appended after it -- swapping it for a
        // different (still well-formed) oid must fail the HMAC check like any other payload
        // tamper, not silently validate with the attacker's substituted oid.
        var service = new SessionTokenService(Secret);
        var token = service.Create(expirySeconds: 900, oid: "33333333-3333-3333-3333-333333333333");
        var forged = service.Create(expirySeconds: 900, oid: "44444444-4444-4444-4444-444444444444");
        var lastDot = token.LastIndexOf('.');
        var forgedLastDot = forged.LastIndexOf('.');
        // Re-attach the ORIGINAL signature to the FORGED (different-oid) payload.
        var tampered = forged[..forgedLastDot] + "." + token[(lastDot + 1)..];

        Assert.False(service.TryValidate(tampered, out var oid));
        Assert.Null(oid);
    }
}

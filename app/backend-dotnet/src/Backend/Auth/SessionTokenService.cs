using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Backend.Auth;

/// <summary>
/// Byte-for-byte port of app/backend/rtmt.py's create_hmac_token/validate_hmac_token
/// (docs/dotnet_mapping.md). Kept intentionally interoperable across backends -- see the #44
/// comment thread -- even though nothing today requires a token minted by one backend to
/// validate on the other: it costs nothing to match the algorithm exactly, and it keeps the
/// door open if #44 is ever resolved the other way.
/// </summary>
public sealed class SessionTokenService(byte[] secret)
{
    private readonly byte[] _secret = secret;

    /// <summary>
    /// Mints a token good for <paramref name="expirySeconds"/> seconds from now (Python default:
    /// 900). The payload is deliberately hand-formatted as <c>{"exp": &lt;n&gt;}</c> (or
    /// <c>{"exp": &lt;n&gt;, "oid": "&lt;oid&gt;"}</c> when <paramref name="oid"/> is supplied,
    /// issue #144/#147, design doc 18.3) -- matching Python's <c>json.dumps({"exp": n, "oid":
    /// oid})</c> byte-for-byte (insertion order exp-then-oid, space after each colon and after the
    /// comma, no trailing whitespace) -- rather than delegated to System.Text.Json's default
    /// (no-space) formatting, so the two backends' tokens are byte-identical for the same secret,
    /// expiry and oid. <paramref name="oid"/> is always a validated Entra object id (a GUID) or
    /// the Development pass-through's synthetic constant -- never attacker-controlled free text --
    /// so no JSON-string escaping is required here, matching rtmt.py's own unescaped f-string.
    /// </summary>
    public string Create(int expirySeconds = 900, string? oid = null)
    {
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expirySeconds;
        var payloadJson = oid is null
            ? $"{{\"exp\": {exp.ToString(CultureInfo.InvariantCulture)}}}"
            : $"{{\"exp\": {exp.ToString(CultureInfo.InvariantCulture)}, \"oid\": \"{oid}\"}}";
        var payloadB64 = UrlSafeBase64Encode(Encoding.UTF8.GetBytes(payloadJson));
        var signature = Sign(payloadB64);
        return $"{payloadB64}.{signature}";
    }

    /// <summary>True iff the signature matches (constant-time) and the token has not expired.</summary>
    public bool Validate(string token) => TryValidate(token, out _);

    /// <summary>
    /// Same validation as <see cref="Validate"/>, additionally returning the payload's <c>oid</c>
    /// claim (issue #147, design doc 18.3's layered session token) -- null when the token is
    /// invalid/expired, or when it is valid but carries no <c>oid</c> (a token minted before #144,
    /// or minted with <paramref name="oid"/> omitted).
    /// </summary>
    public bool TryValidate(string token, out string? oid)
    {
        oid = null;

        var lastDot = token.LastIndexOf('.');
        if (lastDot < 0)
        {
            return false;
        }

        var payloadB64 = token[..lastDot];
        var signature = token[(lastDot + 1)..];

        // PR #96 review nit: Python's hmac.compare_digest(sig, expected_sig) is a plain
        // case-sensitive byte comparison against hexdigest()'s always-lowercase output, so an
        // uppercase-hex signature never matches there. Comparing the presented signature
        // as-is (no .ToLowerInvariant()) against our always-lowercase expectedSignature mirrors
        // that exactly -- a previous draft lowercased the presented signature first, which
        // accepted uppercase hex that Python rejects.
        var expectedSignature = Sign(payloadB64);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(signature),
                Encoding.ASCII.GetBytes(expectedSignature)))
        {
            return false;
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = UrlSafeBase64Decode(payloadB64);
        }
        catch (FormatException)
        {
            return false;
        }

        long exp;
        try
        {
            using var doc = JsonDocument.Parse(payloadBytes);
            exp = doc.RootElement.TryGetProperty("exp", out var expElement) ? expElement.GetInt64() : 0;
            if (doc.RootElement.TryGetProperty("oid", out var oidElement) && oidElement.ValueKind == JsonValueKind.String)
            {
                oid = oidElement.GetString();
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            oid = null;
            return false;
        }

        return true;
    }

    private string Sign(string payloadB64)
    {
        var payloadB64Bytes = Encoding.ASCII.GetBytes(payloadB64);
        var hash = HMACSHA256.HashData(_secret, payloadB64Bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Python's base64.urlsafe_b64encode: standard base64 alphabet with '+'/'/' swapped
    /// for '-'/'_', padding KEPT (unlike many "url-safe" helpers that strip it).</summary>
    private static string UrlSafeBase64Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');

    private static byte[] UrlSafeBase64Decode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/'));
}

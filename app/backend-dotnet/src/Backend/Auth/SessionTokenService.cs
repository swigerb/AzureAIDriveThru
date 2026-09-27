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
    /// 900). The payload is deliberately hand-formatted as <c>{"exp": &lt;n&gt;}</c> -- matching
    /// Python's <c>json.dumps({"exp": n})</c> byte-for-byte (space after the colon, no trailing
    /// whitespace) -- rather than delegated to System.Text.Json's default (no-space) formatting,
    /// so the two backends' tokens are byte-identical for the same secret and expiry.
    /// </summary>
    public string Create(int expirySeconds = 900)
    {
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expirySeconds;
        var payloadJson = $"{{\"exp\": {exp.ToString(CultureInfo.InvariantCulture)}}}";
        var payloadB64 = UrlSafeBase64Encode(Encoding.UTF8.GetBytes(payloadJson));
        var signature = Sign(payloadB64);
        return $"{payloadB64}.{signature}";
    }

    /// <summary>True iff the signature matches (constant-time) and the token has not expired.</summary>
    public bool Validate(string token)
    {
        var lastDot = token.LastIndexOf('.');
        if (lastDot < 0)
        {
            return false;
        }

        var payloadB64 = token[..lastDot];
        var signature = token[(lastDot + 1)..];

        var expectedSignature = Sign(payloadB64);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(signature.ToLowerInvariant()),
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
        }
        catch (JsonException)
        {
            return false;
        }

        return exp > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

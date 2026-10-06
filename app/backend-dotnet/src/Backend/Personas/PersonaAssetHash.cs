using System.Security.Cryptography;

namespace Backend.Personas;

/// <summary>
/// Port of app/backend/app.py's `_content_hash`: a short, stable content hash for the `?v=` query
/// param on persona asset/menu URLs (Rick's PR #102 review item 2). Recomputed fresh from the
/// file's current bytes on every request, never cached across a file edit -- so a stale `?v=` from
/// a URL minted before a pack update simply falls through to the short/no-immutable cache fallback
/// instead of being trusted. 16 hex chars (64 bits) of SHA-256 is plenty of collision resistance
/// for a cache-busting token.
/// </summary>
internal static class PersonaAssetHash
{
    public static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash)[..16];
    }
}

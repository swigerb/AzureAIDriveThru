namespace Backend.Realtime;

/// <summary>
/// Byte-for-byte port of app/backend/rtmt.py's `_origin_matches_host` (docs/dotnet_mapping.md).
/// True iff the `Origin` header's authority (host, and port when non-default) is an *exact*,
/// case-insensitive match for the request's `Host` header -- never a string-suffix/substring
/// match. rtmt.py's own doc comment explains why a suffix check is unsafe: an Origin like
/// `https://evil-&lt;host&gt;` would otherwise pass a naive `origin.endswith(host)` check even
/// though it's a domain the attacker actually controls, not the real host.
/// </summary>
public static class OriginValidator
{
    /// <summary>
    /// An empty/missing `host` can never be a legitimate match -- without this guard, a
    /// bare/schemeless Origin value like the literal string "null" would parse to an empty
    /// authority and incorrectly satisfy an empty `host` comparison (mirrors rtmt.py's own guard
    /// against exactly this, added after a review comment on the original Python fix).
    /// </summary>
    public static bool MatchesHost(string origin, string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Mirrors urllib.parse.urlsplit(origin).netloc -- the authority component only (scheme
        // and path stripped). An Origin that isn't a valid absolute URI (e.g. "null", or a bare
        // hostname a real browser never actually sends as an Origin) has no authority to match,
        // so it is correctly treated as a non-match rather than throwing.
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase);
    }
}

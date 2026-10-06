namespace Backend.Realtime;

/// <summary>
/// Byte-for-byte port of app/backend/rtmt.py's `_origin_matches_host` (docs/dotnet_mapping.md).
/// True iff the `Origin` header's <c>netloc</c> (exactly as `urllib.parse.urlsplit` would parse
/// it -- host, userinfo when present, and port when explicit, with NO default-port stripping) is
/// an *exact*, case-insensitive match for the request's raw `Host` header string -- never a
/// string-suffix/substring match. rtmt.py's own doc comment explains why a suffix check is
/// unsafe: an Origin like `https://evil-&lt;host&gt;` would otherwise pass a naive
/// `origin.endswith(host)` check even though it's a domain the attacker actually controls, not
/// the real host.
///
/// Rick's #230 review item 3: deliberately does NOT use <see cref="Uri"/>/<see
/// cref="Uri.Authority"/> here, even though it reads as the obvious .NET equivalent -- `Authority`
/// silently drops BOTH userinfo (`user:pass@host` -&gt; `host`) and an explicit port that equals
/// the URI scheme's own default (`http://host:80` -&gt; `host`), neither of which
/// `urlsplit(...).netloc` does. Python's raw string compares `"user:pass@host:80"` or
/// `"host:80"` against a bare `"host"` `Host` header and (correctly) finds no match; the old
/// `Uri.Authority`-based comparison here would have wrongly normalised both away and accepted an
/// Origin Python rejects -- an over-permissive gap, not merely a cosmetic difference.
/// </summary>
public static class OriginValidator
{
    private static readonly char[] NetlocTerminators = ['/', '?', '#'];

    /// <summary>
    /// An empty/missing `host` can never be a legitimate match -- without this guard, a
    /// bare/schemeless Origin value like the literal string "null" would parse to an empty
    /// `netloc` and incorrectly satisfy an empty `host` comparison (mirrors rtmt.py's own guard
    /// against exactly this, added after a review comment on the original Python fix).
    /// </summary>
    public static bool MatchesHost(string origin, string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return string.Equals(ExtractNetloc(origin), host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mirrors `urllib.parse.urlsplit(origin).netloc` exactly, operating on the raw string rather
    /// than through <see cref="Uri"/> (which normalises away the very bytes Python preserves):
    /// strips an optional leading "scheme:" (a run of ASCII letters/digits/<c>+-.</c> starting
    /// with a letter, immediately followed by ':' -- Python's own scheme grammar), then, only if
    /// what remains starts with "//", returns everything up to the next '/', '?', '#', or end of
    /// string. Returns "" (never a legitimate match against a non-empty <c>host</c>)
    /// for anything that isn't a "//"-introduced authority at all -- e.g. "null", a bare hostname
    /// with no "//", or a scheme whose own grammar is invalid (so it's never stripped and the
    /// unstripped string then fails the leading-"//" check too).
    /// </summary>
    private static string ExtractNetloc(string origin)
    {
        var rest = origin;
        var colon = rest.IndexOf(':');
        if (colon > 0 && IsValidScheme(rest.AsSpan(0, colon)))
        {
            rest = rest[(colon + 1)..];
        }

        if (rest.Length < 2 || rest[0] != '/' || rest[1] != '/')
        {
            return "";
        }
        rest = rest[2..];

        var end = rest.IndexOfAny(NetlocTerminators);
        return end < 0 ? rest : rest[..end];
    }

    private static bool IsValidScheme(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length == 0 || !char.IsAsciiLetter(candidate[0]))
        {
            return false;
        }
        foreach (var c in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }
        return true;
    }
}

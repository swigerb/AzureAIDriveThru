using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>Byte-for-byte port tests for rtmt.py's `_origin_matches_host` (PR #96 review,
/// required item 1). Exact authority match only -- never a substring/suffix match, since a naive
/// `endswith` check would let an attacker-controlled domain like `https://evil-&lt;host&gt;`
/// through.</summary>
public sealed class OriginValidatorTests
{
    [Fact]
    public void MatchesHost_ExactSchemeAndHost_ReturnsTrue()
    {
        Assert.True(OriginValidator.MatchesHost("https://example.com", "example.com"));
    }

    [Fact]
    public void MatchesHost_IsCaseInsensitive()
    {
        Assert.True(OriginValidator.MatchesHost("https://EXAMPLE.com", "example.com"));
    }

    [Fact]
    public void MatchesHost_DifferentHost_ReturnsFalse()
    {
        Assert.False(OriginValidator.MatchesHost("https://attacker.com", "example.com"));
    }

    [Fact]
    public void MatchesHost_SuffixLookalike_ReturnsFalse()
    {
        // The whole point of an exact-authority match: "evil-example.com" is a domain the
        // attacker actually controls, not a subdomain or alias of "example.com".
        Assert.False(OriginValidator.MatchesHost("https://evil-example.com", "example.com"));
    }

    [Fact]
    public void MatchesHost_DifferentPort_ReturnsFalse()
    {
        Assert.False(OriginValidator.MatchesHost("https://example.com:9999", "example.com"));
    }

    [Fact]
    public void MatchesHost_NonAbsoluteOrigin_ReturnsFalse()
    {
        Assert.False(OriginValidator.MatchesHost("null", "example.com"));
    }

    [Fact]
    public void MatchesHost_EmptyHost_ReturnsFalse()
    {
        Assert.False(OriginValidator.MatchesHost("https://example.com", string.Empty));
    }

    // ── Rick's #230 review item 3: Uri.Authority silently drops userinfo and an explicit
    // default port, which urlsplit(...).netloc does NOT -- these pin the two concrete
    // false-accepts that gap caused. ──

    [Fact]
    public void MatchesHost_OriginWithUserinfo_ReturnsFalseEvenThoughHostPortionMatches()
    {
        // urlsplit("http://user:pass@example.com").netloc == "user:pass@example.com", which is
        // NOT "example.com" -- Uri.Authority drops the "user:pass@" prefix entirely, so the old
        // implementation wrongly accepted this.
        Assert.False(OriginValidator.MatchesHost("http://user:pass@example.com", "example.com"));
    }

    [Fact]
    public void MatchesHost_OriginWithExplicitDefaultPort_ReturnsFalseAgainstAPortlessHost()
    {
        // urlsplit("http://example.com:80").netloc == "example.com:80" -- Python never strips an
        // explicit port just because it equals the scheme's own default. Uri.Authority, however,
        // normalises "http://example.com:80" down to "example.com", so the old implementation
        // wrongly accepted this against a Host header with no port at all.
        Assert.False(OriginValidator.MatchesHost("http://example.com:80", "example.com"));
    }

    [Fact]
    public void MatchesHost_OriginWithExplicitDefaultPort_ReturnsTrueAgainstTheSameExplicitPort()
    {
        // Sanity check on the other side of the above: when the Host header itself also carries
        // the explicit port, the exact string match succeeds as normal.
        Assert.True(OriginValidator.MatchesHost("http://example.com:80", "example.com:80"));
    }

    [Fact]
    public void MatchesHost_SchemeStartingWithDigit_HasNoNetlocAndReturnsFalse()
    {
        // urlsplit requires a scheme's first character to be a letter; "1abc://host" therefore
        // has NO recognised scheme, the whole string doesn't start with "//", and so has no
        // netloc at all -- never a match against a non-empty host.
        Assert.False(OriginValidator.MatchesHost("1abc://host", "host"));
    }

    [Fact]
    public void MatchesHost_SchemeRelativeOrigin_StillExtractsNetloc()
    {
        // urlsplit("//host:80").netloc == "host:80" even with no scheme at all, since the
        // leading "//" alone introduces an authority component.
        Assert.True(OriginValidator.MatchesHost("//host:80", "host:80"));
    }
}

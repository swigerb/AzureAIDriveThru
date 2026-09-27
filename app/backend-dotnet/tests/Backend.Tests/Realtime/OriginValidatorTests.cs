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
}

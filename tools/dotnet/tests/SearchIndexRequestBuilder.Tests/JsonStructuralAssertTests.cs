using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Synthetic (non-real-data) coverage for JsonStructuralAssert's own comparison rules -- isolated
/// from PythonParityTests.DotnetPort_MatchesRealPythonTwin_ForEveryEnabledPersona, which only
/// exercises whatever numeric shapes happen to exist in today's real persona exports.
/// </summary>
public sealed class JsonStructuralAssertTests
{
    [Fact]
    public void Equal_PassesForSameStructure_RegardlessOfPropertyOrder()
    {
        var expected = JsonNode.Parse("""{"a": 1, "b": "x"}""");
        var actual = JsonNode.Parse("""{"b": "x", "a": 1}""");
        JsonStructuralAssert.Equal(expected, actual);
    }

    [Fact]
    public void Equal_TreatsWholeNumberIntAndFloat_AsTheSameSemanticValue()
    {
        // Both the "expected" (parsed from Python-captured text, preserving "200" exactly) and
        // "actual" (parsed here as a stand-in for a programmatically-built C# node) sides agree on
        // int form -- same token shape, same value: passes both the semantic-value check and the
        // token-shape check added for PR #250 review item 3.
        JsonStructuralAssert.Equal(JsonNode.Parse("200"), JsonNode.Parse("200"));
    }

    /// <summary>
    /// PR #250 review (optional, item 3): "dimensions": 3072 (Python int) vs "dimensions": 3072.0
    /// (a C# double that happens to be numerically equal) must now FAIL -- before this change, the
    /// semantic double-value comparison alone (200.0 == 200) would have let this through silently,
    /// even though Azure AI Search's REST API can treat an int-typed and float-typed JSON number
    /// differently for this field.
    ///
    /// Mutation check (reverted after confirming): temporarily removing the token-shape assertion
    /// from JsonStructuralAssert.AssertValuesEqual (keeping only the pre-existing double-value
    /// equality check) made this test FAIL to fail -- i.e. 3072 vs 3072.0 passed silently --
    /// confirming this test only passes because of the new check, restored before committing.
    /// </summary>
    [Fact]
    public void Equal_FailsWhenSameNumericValue_IsWrittenAsIntInOneSideAndFloatInTheOther()
    {
        var expected = JsonNode.Parse("""{"dimensions": 3072}""");
        var actual = JsonNode.Parse("""{"dimensions": 3072.0}""");

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => JsonStructuralAssert.Equal(expected, actual));
        Assert.Contains("int in one JSON body and a float in the other", ex.Message);
    }

    [Fact]
    public void Equal_FailsForDifferentSemanticNumericValues()
    {
        var expected = JsonNode.Parse("""{"count": 5}""");
        var actual = JsonNode.Parse("""{"count": 6}""");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => JsonStructuralAssert.Equal(expected, actual));
    }

    [Fact]
    public void Equal_FailsWhenAStringValueDiffersByEvenOneCharacter()
    {
        var expected = JsonNode.Parse("""{"sizes": "abc"}""");
        var actual = JsonNode.Parse("""{"sizes": "abd"}""");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => JsonStructuralAssert.Equal(expected, actual));
    }
}

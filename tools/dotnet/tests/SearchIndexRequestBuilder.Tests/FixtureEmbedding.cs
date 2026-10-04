using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Deterministic, pure-function stand-in for <c>generate_embeddings</c>'s real Azure OpenAI call
/// (setup_search_index.py lines 286-291) -- the "client-seam" from docs/dotnet_tooling.md's design
/// note. Both this test project's capture-parity test and the Python capture harness
/// (Fixtures/capture_search_index_requests.py) substitute this SAME formula wherever the real
/// script would call <c>openai_client.embeddings.create</c>, so the resulting document bodies
/// (whose "embedding" field depends on it) are reproducible and byte-comparable without ever
/// calling a live embedding endpoint.
///
/// TEST-ONLY (PR #250 review R4): lives in this test project, not in the shipped
/// SearchIndexRequestBuilder tool -- a real CLI run never calls Azure OpenAI under any flag, so it
/// has no embedding formula to compile in; only <see cref="PythonParityTests"/> injects this
/// fixture into <see cref="SearchIndexRequestPlanner.BuildPlan"/>'s optional
/// <c>embeddingProvider</c> parameter, to match the capture harness's own identical fixture.
///
/// Deliberately NOT a 3072-dimension vector like a real <c>text-embedding-3-large</c> embedding
/// (EMBEDDING_DIMENSIONS, setup_search_index.py line ~64): the point of this fixture is to make the
/// REQUEST-BUILDING code path identical and testable end to end, not to emulate real embedding
/// content (which is non-deterministic model output this port has no way to reproduce, and doesn't
/// need to -- the index definition's own "dimensions": 3072 comes from the real constant
/// independently, in SearchIndexDefinitionBuilder, and is unaffected by this fixture's length). This
/// divergence is recorded in docs/dotnet_tooling.md.
/// </summary>
internal static class FixtureEmbedding
{
    /// <summary>Deliberately small (not 3072) -- see the type-level remarks above.</summary>
    public const int Dimensions = 8;

    /// <summary>
    /// <c>sha256(text)</c>'s first <see cref="Dimensions"/> bytes, each mapped from [0, 255] to
    /// [-1, 1] and rounded to 6 decimal places via fixed-point text formatting (not a raw double
    /// round, so both runtimes land on the exact same bit pattern for the final value).
    /// </summary>
    public static IReadOnlyList<double> For(string text)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var values = new double[Dimensions];
        for (var i = 0; i < Dimensions; i++)
        {
            var raw = (digest[i] / 255.0 * 2) - 1;
            values[i] = double.Parse(raw.ToString("F6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
        return values;
    }
}

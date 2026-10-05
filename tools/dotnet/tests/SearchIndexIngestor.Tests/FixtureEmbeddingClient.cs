using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// Deterministic, test-only <see cref="IEmbeddingClient"/> fake -- the "deterministic fakes only in
/// the test project" seam the task calls for (production never ships a fake; see
/// AzureOpenAiEmbeddingClient.cs's own remarks for the one real implementation). Uses the EXACT
/// same sha256-based formula as SearchIndexRequestBuilder.Tests/FixtureEmbedding.cs and
/// Fixtures/capture_search_index_ingestion.py's own fixture_embedding(), so request bodies built
/// from it are directly comparable across all three.
/// </summary>
internal sealed class FixtureEmbeddingClient : IEmbeddingClient
{
    /// <summary>Deliberately small (not the real 3072) -- see
    /// SearchIndexRequestBuilder.Tests/FixtureEmbedding.cs's remarks for why.</summary>
    public const int Dimensions = 8;

    public Task<IReadOnlyList<IReadOnlyList<double>>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts, string deployment, CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyList<double>> result = texts.Select(For).ToList();
        return Task.FromResult(result);
    }

    /// <summary><c>sha256(text)</c>'s first <see cref="Dimensions"/> bytes, each mapped from
    /// [0, 255] to [-1, 1] and rounded to 6 decimal places via fixed-point text formatting (not a
    /// raw double round), so both this fixture and the Python capture harness land on the exact
    /// same IEEE-754 value.</summary>
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

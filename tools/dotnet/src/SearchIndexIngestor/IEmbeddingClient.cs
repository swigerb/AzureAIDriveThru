namespace SearchIndexIngestor;

/// <summary>
/// The injectable "client-seam" interface the task explicitly asks for: faithful port of
/// setup_search_index.py's <c>generate_embeddings</c> (lines 286-291) --
/// <c>openai_client.embeddings.create(input=texts, model=deployment)</c>, ONE batch call for every
/// text at once (never chunked, unlike the 100-document search-upload batching). Exactly one real
/// implementation ships in production (<see cref="AzureOpenAiEmbeddingClient"/>, a genuine Azure
/// OpenAI REST call authenticated via <c>DefaultAzureCredential</c>); only
/// <c>tools/dotnet/tests/SearchIndexIngestor.Tests</c> substitutes a deterministic fake (see that
/// project's <c>FixtureEmbeddingClient</c>), matching the task's "deterministic fakes only in the
/// test project" instruction -- there is no live-embedding fixture or non-deterministic stand-in
/// shipped in this production assembly.
/// </summary>
public interface IEmbeddingClient
{
    /// <param name="texts">One combined embedding-input string per document, in the SAME order as
    /// the documents they belong to (<see cref="PersonaIngestPlan.TextsForEmbedding"/>) --
    /// callers zip the returned embeddings back onto their documents by this same positional
    /// order, exactly like <c>ingest_plan</c>'s own <c>zip(plan.documents, embeddings)</c>.</param>
    /// <param name="deployment">The Azure OpenAI deployment name to embed with (resolved by
    /// <c>OpenAiSettingsResolver</c>).</param>
    /// <returns>One embedding vector per input text, same order, same length as
    /// <paramref name="texts"/>.</returns>
    Task<IReadOnlyList<IReadOnlyList<double>>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts, string deployment, CancellationToken cancellationToken);
}

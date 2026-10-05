namespace SearchIndexIngestor.Tests;

/// <summary>
/// Fixed, test-only stand-ins for the Azure OpenAI endpoint/embedding-deployment and Azure AI
/// Search endpoint this test project's parity/unit tests pass around -- never read from a real
/// environment. Must stay identical to Fixtures/capture_search_index_ingestion.py's own
/// FAKE_OPENAI_ENDPOINT/FAKE_EMBEDDING_DEPLOYMENT/FAKE_SEARCH_ENDPOINT constants, since both sides'
/// captured/reconstructed request bodies embed these values directly (the index definition's
/// vectorizer fields, and the request URL's host).
/// </summary>
internal static class TestFixtureValues
{
    public const string FakeSearchEndpoint = "https://fake.search.windows.net";
    public const string FakeOpenAiEndpoint = "https://fake.openai.azure.com";
    public const string FakeEmbeddingDeployment = "fake-embedding-deployment";
}

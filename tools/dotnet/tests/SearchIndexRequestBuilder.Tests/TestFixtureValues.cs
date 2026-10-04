namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Fixed, test-only stand-ins for <c>AZURE_OPENAI_EASTUS2_ENDPOINT</c>/
/// <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> (setup_search_index.py's <c>run()</c>, lines 471-473).
///
/// TEST-ONLY (PR #250 review R4): used ONLY by <see cref="PythonParityTests"/>, so its
/// independent <see cref="SearchIndexRequestPlanner.BuildPlan"/> reconstruction and the capture
/// harness's (Fixtures/capture_search_index_requests.py) own captured index-definition bodies use
/// the exact same literal strings for the vectorizer's resourceUri/deploymentId fields -- these
/// flow straight into that comparison, so any mismatch here would fail the parity test for a
/// reason that has nothing to do with the port's own correctness. A real CLI run instead resolves
/// these from the real environment/CLI flags via <c>OpenAiSettingsResolver</c> (production, not
/// test-only) -- see docs/dotnet_tooling.md's "Batch 2 port" section.
/// </summary>
internal static class TestFixtureValues
{
    /// <summary>Must stay identical to capture_search_index_requests.py's own
    /// <c>FAKE_OPENAI_ENDPOINT</c>.</summary>
    public const string FakeOpenAiEndpoint = "https://fake.openai.azure.com";

    /// <summary>Must stay identical to capture_search_index_requests.py's own
    /// <c>FAKE_EMBEDDING_DEPLOYMENT</c>.</summary>
    public const string FakeEmbeddingDeployment = "fake-embedding-deployment";
}

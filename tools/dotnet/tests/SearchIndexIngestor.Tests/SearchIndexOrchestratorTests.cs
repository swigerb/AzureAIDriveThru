using System.Net;
using System.Text.Json.Nodes;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// C#-only unit tests for <see cref="SearchIndexOrchestrator"/> -- the <c>ingest_plan</c> port
/// (setup_search_index.py lines 413-445): create/update index -> embed -> upload in 100-doc
/// batches -> delete stale -> verify count (with retry). Strict Python-request-parity for the
/// index-create-or-update and upload-batch-loop pieces is covered separately by
/// <see cref="PythonParityTests"/>; these tests instead verify the FULL orchestration sequencing
/// (ordering, batch boundaries beyond 100 documents, retry/give-up behaviour) against a fake HTTP
/// handler, since that full-flow behaviour is this port's own addition with no single Python
/// function to capture it against end to end.
/// </summary>
public sealed class SearchIndexOrchestratorTests
{
    private static JsonObject Document(string id) => new() { ["id"] = id, ["category"] = "c", ["name"] = id };

    private static PersonaIngestPlan MakePlan(string personaId, string indexName, int documentCount)
    {
        var documents = Enumerable.Range(0, documentCount).Select(i => Document($"doc-{i}")).ToList();
        var texts = Enumerable.Range(0, documentCount).Select(i => $"text-{i}").ToList();
        return new PersonaIngestPlan(personaId, indexName, documents, texts);
    }

    [Fact]
    public async Task IngestAsync_CreatesIndex_UploadsAllDocuments_DeletesStale_AndVerifiesCount()
    {
        var plan = MakePlan("alpha", "alpha-index", documentCount: 3);
        var log = new StringWriter();

        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, request.Body!);
            }
            if (request.Uri.Contains("search.index"))
            {
                var value = JsonNode.Parse(request.Body!)!["value"]!.AsArray();
                var results = value.Select(d => new JsonObject { ["key"] = d!["id"]!.DeepClone(), ["status"] = true });
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(results.ToArray()) }.ToJsonString());
            }
            if (request.Uri.Contains("search.post.search"))
            {
                // No existing documents in the index yet -- nothing stale to delete.
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
            }
            if (request.Uri.Contains("$count"))
            {
                return FakeHttpMessageHandler.PlainText(HttpStatusCode.OK, "3");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.Uri}");
        });

        using var searchClient = new SearchIndexHttpClient(new HttpClient(handler), "https://fake.search.windows.net");
        var embeddingClient = new FixtureEmbeddingClient();
        var orchestrator = new SearchIndexOrchestrator(searchClient, embeddingClient, "https://fake.openai.azure.com", "fake-deployment", log);

        await orchestrator.IngestAsync(plan, CancellationToken.None);

        Assert.Contains("deleted 0 stale document(s)", log.ToString());
        Assert.Contains("search index setup complete", log.ToString());
        // Every document got a real (fixture) embedding attached before upload.
        Assert.All(plan.Documents, d => Assert.True(d["embedding"]!.AsArray().Count == FixtureEmbeddingClient.Dimensions));
    }

    /// <summary>Test-only <see cref="IEmbeddingClient"/> fake that returns a fixed vector
    /// containing whole-number components (1.0, -1.0, 0.0) -- the values that trigger
    /// System.Text.Json's "-1.0" -> "-1" decimal-dropping quirk SearchIndexOrchestrator.cs's own
    /// remarks describe.</summary>
    private sealed class FixedVectorEmbeddingClient : IEmbeddingClient
    {
        public Task<IReadOnlyList<IReadOnlyList<double>>> GenerateEmbeddingsAsync(
            IReadOnlyList<string> texts, string deployment, CancellationToken cancellationToken)
        {
            IReadOnlyList<IReadOnlyList<double>> result = texts.Select(_ => (IReadOnlyList<double>)new[] { 1.0, -1.0, 0.0, 2.5 }).ToList();
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task IngestAsync_WritesWholeNumberEmbeddingComponents_WithAPythonFaithfulDecimalPoint()
    {
        // Mutation check performed: temporarily reverted the "embedding" vector-building loop in
        // SearchIndexOrchestrator.cs back to plain `vector.Add(component)` (dropping the
        // JsonNode.Parse(PythonFloatRepr(...)) fix) -- this test failed, asserting "1" where "1.0"
        // was expected, exactly reproducing the divergence PythonParityTests.cs's real-Python-twin
        // comparison first caught. Reverted back to the fix afterwards; both this test and
        // PythonParityTests.cs pass again.
        var plan = MakePlan("alpha", "alpha-index", documentCount: 1);
        string? uploadedBody = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, request.Body!);
            }
            if (request.Uri.Contains("search.index"))
            {
                uploadedBody = request.Body;
                var value = JsonNode.Parse(request.Body!)!["value"]!.AsArray();
                var results = value.Select(d => new JsonObject { ["key"] = d!["id"]!.DeepClone(), ["status"] = true });
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(results.ToArray()) }.ToJsonString());
            }
            if (request.Uri.Contains("search.post.search"))
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
            }
            if (request.Uri.Contains("$count"))
            {
                return FakeHttpMessageHandler.PlainText(HttpStatusCode.OK, "1");
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.Uri}");
        });

        using var searchClient = new SearchIndexHttpClient(new HttpClient(handler), "https://fake.search.windows.net");
        var orchestrator = new SearchIndexOrchestrator(
            searchClient, new FixedVectorEmbeddingClient(), "https://fake.openai.azure.com", "fake-deployment", TextWriter.Null);

        await orchestrator.IngestAsync(plan, CancellationToken.None);

        Assert.NotNull(uploadedBody);
        var embedding = JsonNode.Parse(uploadedBody!)!["value"]![0]!["embedding"]!.AsArray();
        Assert.Equal(["1.0", "-1.0", "0.0", "2.5"], embedding.Select(v => v!.ToJsonString()));
    }

    [Fact]
    public async Task IngestAsync_UploadsInBatchesOf100_ForMoreThan100Documents()
    {
        var plan = MakePlan("alpha", "alpha-index", documentCount: 150);
        var uploadBatchSizes = new List<int>();

        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, request.Body!);
            }
            if (request.Uri.Contains("search.index"))
            {
                var value = JsonNode.Parse(request.Body!)!["value"]!.AsArray();
                uploadBatchSizes.Add(value.Count);
                var results = value.Select(d => new JsonObject { ["key"] = d!["id"]!.DeepClone(), ["status"] = true });
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(results.ToArray()) }.ToJsonString());
            }
            if (request.Uri.Contains("search.post.search"))
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
            }
            return FakeHttpMessageHandler.PlainText(HttpStatusCode.OK, "150");
        });

        using var searchClient = new SearchIndexHttpClient(new HttpClient(handler), "https://fake.search.windows.net");
        var orchestrator = new SearchIndexOrchestrator(searchClient, new FixtureEmbeddingClient(), "https://fake.openai.azure.com", "fake-deployment", TextWriter.Null);

        await orchestrator.IngestAsync(plan, CancellationToken.None);

        Assert.Equal([100, 50], uploadBatchSizes);
    }

    [Fact]
    public async Task IngestAsync_DeletesDocumentsNoLongerInThePlan()
    {
        var plan = MakePlan("alpha", "alpha-index", documentCount: 2); // ids doc-0, doc-1
        var deletedIds = new List<string>();

        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, request.Body!);
            }
            if (request.Uri.Contains("search.post.search"))
            {
                return FakeHttpMessageHandler.Json(
                    HttpStatusCode.OK, """{"value":[{"id":"doc-0"},{"id":"doc-1"},{"id":"stale-doc"}]}""");
            }
            if (request.Uri.Contains("search.index"))
            {
                var value = JsonNode.Parse(request.Body!)!["value"]!.AsArray();
                if (value[0]!["@search.action"]!.GetValue<string>() == "delete")
                {
                    deletedIds.AddRange(value.Select(d => d!["id"]!.GetValue<string>()));
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
                }
                var results = value.Select(d => new JsonObject { ["key"] = d!["id"]!.DeepClone(), ["status"] = true });
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(results.ToArray()) }.ToJsonString());
            }
            return FakeHttpMessageHandler.PlainText(HttpStatusCode.OK, "2");
        });

        using var searchClient = new SearchIndexHttpClient(new HttpClient(handler), "https://fake.search.windows.net");
        var log = new StringWriter();
        var orchestrator = new SearchIndexOrchestrator(searchClient, new FixtureEmbeddingClient(), "https://fake.openai.azure.com", "fake-deployment", log);

        await orchestrator.IngestAsync(plan, CancellationToken.None);

        Assert.Equal(["stale-doc"], deletedIds);
        Assert.Contains("deleted 1 stale document(s)", log.ToString());
    }

    [Fact]
    public async Task IngestAsync_RetriesCountVerification_ThenSucceeds_WhenCountCatchesUpLate()
    {
        var plan = MakePlan("alpha", "alpha-index", documentCount: 1);
        var countCallCount = 0;
        var delayCalls = 0;

        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, request.Body!);
            }
            if (request.Uri.Contains("search.post.search"))
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
            }
            if (request.Uri.Contains("search.index"))
            {
                var value = JsonNode.Parse(request.Body!)!["value"]!.AsArray();
                var results = value.Select(d => new JsonObject { ["key"] = d!["id"]!.DeepClone(), ["status"] = true });
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(results.ToArray()) }.ToJsonString());
            }
            countCallCount++;
            return FakeHttpMessageHandler.PlainText(HttpStatusCode.OK, countCallCount < 3 ? "0" : "1");
        });

        using var searchClient = new SearchIndexHttpClient(new HttpClient(handler), "https://fake.search.windows.net");
        var orchestrator = new SearchIndexOrchestrator(
            searchClient, new FixtureEmbeddingClient(), "https://fake.openai.azure.com", "fake-deployment", TextWriter.Null,
            delay: (_, _) => { delayCalls++; return Task.CompletedTask; });

        await orchestrator.IngestAsync(plan, CancellationToken.None);

        Assert.Equal(3, countCallCount);
        Assert.Equal(2, delayCalls);
    }

    [Fact]
    public async Task IngestAsync_Throws_WhenCountNeverMatches_AfterAllAttempts()
    {
        var plan = MakePlan("alpha", "alpha-index", documentCount: 5);

        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, request.Body!);
            }
            if (request.Uri.Contains("search.post.search"))
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"value":[]}""");
            }
            if (request.Uri.Contains("search.index"))
            {
                var value = JsonNode.Parse(request.Body!)!["value"]!.AsArray();
                var results = value.Select(d => new JsonObject { ["key"] = d!["id"]!.DeepClone(), ["status"] = true });
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(results.ToArray()) }.ToJsonString());
            }
            return FakeHttpMessageHandler.PlainText(HttpStatusCode.OK, "0"); // never catches up to 5
        });

        using var searchClient = new SearchIndexHttpClient(new HttpClient(handler), "https://fake.search.windows.net");
        var orchestrator = new SearchIndexOrchestrator(
            searchClient, new FixtureEmbeddingClient(), "https://fake.openai.azure.com", "fake-deployment", TextWriter.Null,
            delay: (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<Exception>(() => orchestrator.IngestAsync(plan, CancellationToken.None));
        Assert.Contains("holds 0 document(s)", ex.Message);
        Assert.Contains("the plan has 5", ex.Message);
    }

    /// <summary>Test-only <see cref="IEmbeddingClient"/> fake that always returns a fixed number of
    /// embeddings, regardless of how many texts it's asked to embed -- used to force the
    /// count-mismatch guard (Rick's review, item 3/round 2).</summary>
    private sealed class FixedCountEmbeddingClient(int count) : IEmbeddingClient
    {
        public Task<IReadOnlyList<IReadOnlyList<double>>> GenerateEmbeddingsAsync(
            IReadOnlyList<string> texts, string deployment, CancellationToken cancellationToken)
        {
            IReadOnlyList<IReadOnlyList<double>> result =
                Enumerable.Range(0, count).Select(i => (IReadOnlyList<double>)FixtureEmbeddingClient.For($"fixed-{i}")).ToList();
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task IngestAsync_Throws_WhenAzureOpenAiReturnsFewerEmbeddingsThanDocuments()
    {
        // setup_search_index.py's own zip(plan.documents, embeddings) would silently truncate to
        // the shorter sequence here instead of failing (ingest_plan, line 435) -- this port
        // deliberately diverges by raising a clean, catchable error instead of letting the
        // embedding-attachment loop below throw an unhandled index-out-of-range exception.
        var plan = MakePlan("alpha", "alpha-index", documentCount: 3);
        var handler = new FakeHttpMessageHandler(request =>
            request.Method == HttpMethod.Put
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, request.Body!)
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        using var searchClient = new SearchIndexHttpClient(new HttpClient(handler), "https://fake.search.windows.net");
        var orchestrator = new SearchIndexOrchestrator(
            searchClient, new FixedCountEmbeddingClient(count: 2), "https://fake.openai.azure.com", "fake-deployment", TextWriter.Null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.IngestAsync(plan, CancellationToken.None));

        Assert.Contains("returned 2 embedding(s)", ex.Message);
        Assert.Contains("for 3 document(s)", ex.Message);
    }
}

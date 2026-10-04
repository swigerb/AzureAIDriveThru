using System.Text.Json;
using System.Text.Json.Nodes;
using SearchIndexRequestBuilder;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// Strict Python-twin parity for the two pieces of SearchIndexHttpClient this task's test
/// requirement names explicitly: "index create-or-update and the upload-batch loop" -- method,
/// URL (incl. index name and api-version), headers that matter, bodies, and batch boundaries.
/// The REAL, unmodified app/backend/setup_search_index.py is driven as a genuine subprocess
/// (Fixtures/capture_search_index_ingestion.py, a thin HTTP-transport-capture harness around the
/// twin's own real, read-only build_plan plus its real create_or_update_index/upload_documents --
/// see that file's own docstring), with no live Azure/OpenAI call on either side, ever.
///
/// delete_stale_documents/verify_document_count are deliberately OUT of scope for this strict
/// comparison (see SearchIndexHttpClientTests.cs/SearchIndexOrchestratorTests.cs's own remarks for
/// why -- those two are about real index STATE/pagination, not a deterministic request-building
/// concern a captured fixture could usefully pin down).
///
/// Mutation checks actually performed while implementing this port (each reverted immediately
/// after confirming the expected failure, then re-verified green):
/// * Changed SearchIndexHttpClient.cs's ApiVersion from "2026-04-01" to "2023-11-01" -- every
///   persona's URL assertion failed with the expected "api-version=2023-11-01" vs
///   "api-version=2026-04-01" mismatch.
/// * Changed CreateOrUpdateIndexAsync's "Prefer: return=representation" header to
///   "return=minimal" -- the header-comparison assertion failed with the expected
///   "return=representation" vs "return=minimal" mismatch.
/// * Changed this test's own upload-batch loop from 100- to 99-document slices (standing in for a
///   DocumentBatchBuilder.cs/SearchIndexOrchestrator.cs batch-size regression) -- the "sonic"
///   persona's (180 documents) first batch failed with "array length differs. expected 100,
///   actual 99."
/// * Reverted SearchIndexOrchestrator.cs's embedding-vector loop from
///   <c>JsonNode.Parse(PythonFloatRepr(component))</c> back to plain <c>vector.Add(component)</c>
///   -- this is how the "1" vs "1.0" int/float-shape divergence documented below was actually
///   *found* in the first place (not a check added after the fact): the fix is also covered by a
///   dedicated C#-only unit test, SearchIndexOrchestratorTests.cs's
///   <c>IngestAsync_WritesWholeNumberEmbeddingComponents_WithAPythonFaithfulDecimalPoint</c>, since
///   this parity test's own embedding-attachment loop (below) is independent of
///   SearchIndexOrchestrator.cs's and wouldn't itself catch a regression there.
/// </summary>
public sealed class PythonParityTests
{
    [Fact]
    public async Task DotnetPort_MatchesRealPythonTwin_ForIndexCreateOrUpdate_AndUploadBatchLoop()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var harnessScript = Path.Combine(
            repoRoot, "tools", "dotnet", "tests", "SearchIndexIngestor.Tests", "Fixtures",
            "capture_search_index_ingestion.py");
        Assert.True(File.Exists(harnessScript), $"Capture harness not found: {harnessScript}");

        var interpreter = await PythonInterop.RequireInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        var (exitCode, stdout, stderr) = await PythonInterop.RunAsync(
            interpreter, [harnessScript], TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Capture harness exited {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        using var captured = JsonDocument.Parse(stdout);
        var capturedPersonas = captured.RootElement.GetProperty("personas");

        var dotnetPersonas = EnabledPersonaDiscovery.DiscoverAll(repoRoot);
        var capturedPersonaIds = capturedPersonas.EnumerateObject().Select(p => p.Name).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var dotnetPersonaIds = dotnetPersonas.Select(p => p.PersonaId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(capturedPersonaIds, dotnetPersonaIds);
        Assert.True(dotnetPersonaIds.Count > 0, "Expected at least one enabled persona in the real repo.");

        foreach (var persona in dotnetPersonas)
        {
            var plan = PersonaIngestPlanBuilder.Build(persona);
            var capturedPersona = capturedPersonas.GetProperty(persona.PersonaId);
            Assert.Equal(capturedPersona.GetProperty("index_name").GetString(), plan.IndexName);
            Assert.Equal(capturedPersona.GetProperty("document_count").GetInt32(), plan.DocumentCount);

            // Attach the SAME deterministic fixture embedding fixture_embedding()/
            // FixtureEmbeddingClient.For() both sides use, mirroring where ingest_plan's own
            // zip(plan.documents, embeddings) would attach real ones -- using the same
            // Python-faithful float formatting as SearchIndexOrchestrator.cs's own production code
            // (see its remarks: JsonValue.Create(double) drops the decimal point for whole-number
            // values, unlike Python's json.dumps, so this test must match production's fix or it
            // would pass/fail for reasons unrelated to the production code it's meant to verify).
            for (var i = 0; i < plan.Documents.Count; i++)
            {
                var vector = new JsonArray();
                foreach (var component in FixtureEmbeddingClient.For(plan.TextsForEmbedding[i]))
                {
                    vector.Add(JsonNode.Parse(PythonJsonDumps.PythonFloatRepr(component)));
                }
                plan.Documents[i]["embedding"] = vector;
            }

            var handler = new FakeHttpMessageHandler(request =>
                request.Method == HttpMethod.Put
                    ? FakeHttpMessageHandler.Json(System.Net.HttpStatusCode.OK, request.Body!)
                    : FakeHttpMessageHandler.Json(
                        System.Net.HttpStatusCode.OK,
                        new JsonObject
                        {
                            ["value"] = new JsonArray(
                                JsonNode.Parse(request.Body!)!["value"]!.AsArray()
                                    .Select(d => (JsonNode)new JsonObject { ["key"] = d!["id"]!.DeepClone(), ["status"] = true })
                                    .ToArray()),
                        }.ToJsonString()));
            using var client = new SearchIndexHttpClient(new HttpClient(handler), TestFixtureValues.FakeSearchEndpoint);

            var indexDefinition = SearchIndexDefinitionBuilder.Build(
                plan.IndexName, TestFixtureValues.FakeOpenAiEndpoint, TestFixtureValues.FakeEmbeddingDeployment);
            await client.CreateOrUpdateIndexAsync(plan.IndexName, indexDefinition, CancellationToken.None);

            for (var i = 0; i < plan.Documents.Count; i += 100)
            {
                var batchDocuments = plan.Documents.Skip(i).Take(100).ToList();
                var batchBody = DocumentBatchBuilder.BuildBatches(batchDocuments).Single();
                await client.UploadBatchAsync(plan.IndexName, batchBody, CancellationToken.None);
            }

            var capturedIndexRequest = capturedPersona.GetProperty("index_request");
            var capturedUploadRequests = capturedPersona.GetProperty("upload_batch_requests");
            // 1 index-create-or-update request, plus one upload-batch request per 100-document
            // slice -- same batch boundary on both sides (DocumentBatchBuilder.cs's BatchSize).
            Assert.Equal(1 + capturedUploadRequests.GetArrayLength(), handler.Requests.Count);

            AssertRequestMatches(capturedIndexRequest, handler.Requests[0], $"personas.{persona.PersonaId}.index_request");
            for (var i = 0; i < capturedUploadRequests.GetArrayLength(); i++)
            {
                AssertRequestMatches(
                    capturedUploadRequests[i], handler.Requests[i + 1], $"personas.{persona.PersonaId}.upload_batch_requests[{i}]");
            }
        }
    }

    private static void AssertRequestMatches(JsonElement capturedRequest, CapturedRequest dotnetRequest, string path)
    {
        Assert.Equal(capturedRequest.GetProperty("method").GetString(), dotnetRequest.Method.Method);
        Assert.Equal(capturedRequest.GetProperty("url").GetString(), dotnetRequest.Uri);

        foreach (var header in capturedRequest.GetProperty("headers").EnumerateObject())
        {
            Assert.True(
                dotnetRequest.Headers.TryGetValue(header.Name, out var dotnetValue),
                $"At {path}: expected a '{header.Name}' header, but the .NET request had none.");
            // Whitespace-normalized comparison: .NET's typed HttpHeaders re-serializes
            // "type;param" as "type; param" (a space after the semicolon) even when the literal
            // added via TryAddWithoutValidation had none -- a header-formatting artifact, not a
            // wire-format difference this comparison needs to fail on.
            Assert.Equal(
                Normalize(header.Value.GetString()), Normalize(dotnetValue),
                ignoreCase: true);
        }

        var capturedBody = capturedRequest.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(bodyElement.GetRawText())
            : null;
        var dotnetBody = dotnetRequest.Body is null ? null : JsonNode.Parse(dotnetRequest.Body);
        JsonStructuralAssert.Equal(capturedBody, dotnetBody, $"{path}.body");
    }

    private static string Normalize(string? value) => (value ?? "").Replace(" ", "");

    private static readonly System.Text.RegularExpressions.Regex DryRunLine = new(
        @"\[dry-run\] persona '([^']+)': index '([^']+)', (\d+) document\(s\) planned");

    /// <summary>
    /// Content-level stdout/exit-code parity for <c>--dry-run</c>: the real, unmodified
    /// setup_search_index.py is spawned as a genuine subprocess (no fixture harness needed here --
    /// dry-run makes zero Azure calls and needs no env vars, per its own help text and
    /// run()'s own docstring, lines 455-457), and its stdout is compared against an in-process
    /// <see cref="CliRunner.RunAsync"/> call over the SAME real repo's enabled persona catalog.
    /// Compared at the regex-extracted (persona, index-name, document-count) level, not literal
    /// string equality -- Python's RichHandler-formatted log lines (timestamps, colour codes) are
    /// not byte-reproducible by this port, nor should they be; the CONTENT is what matters.
    /// </summary>
    [Fact]
    public async Task DryRun_MatchesRealPythonTwin_ForEveryEnabledPersona_AtTheStdoutContentLevel()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var scriptPath = Path.Combine(repoRoot, "app", "backend", "setup_search_index.py");
        Assert.True(File.Exists(scriptPath), $"Python twin not found: {scriptPath}");

        var interpreter = await PythonInterop.RequireInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        var (pyExitCode, pyStdout, pyStderr) = await PythonInterop.RunAsync(
            interpreter, [scriptPath, "--dry-run"], TestContext.Current.CancellationToken,
            // Rich (setup_search_index.py's RichHandler) word-wraps each log line to the detected
            // console width when stdout isn't a real terminal (narrow by default), splitting a
            // single dry-run line's message across several physical lines -- a presentation detail
            // this comparison doesn't care about. A very wide COLUMNS keeps every dry-run line on
            // one physical line so the regex below can match it whole.
            extraEnvironment: new Dictionary<string, string> { ["COLUMNS"] = "1000" });
        Assert.True(pyExitCode == 0, $"Python --dry-run exited {pyExitCode}.\nstdout:\n{pyStdout}\nstderr:\n{pyStderr}");

        var dotnetStdout = new StringWriter();
        var dotnetStderr = new StringWriter();
        var dotnetExitCode = await CliRunner.RunAsync(["--dry-run"], dotnetStdout, dotnetStderr, repoRootOverride: repoRoot);
        Assert.Equal(0, dotnetExitCode);
        Assert.Equal("", dotnetStderr.ToString());

        var pyTriples = ExtractDryRunTriples(pyStdout);
        var dotnetTriples = ExtractDryRunTriples(dotnetStdout.ToString());
        Assert.True(pyTriples.Count > 0, $"Expected at least one '[dry-run] persona ...' line in Python's stdout, got:\n{pyStdout}");
        Assert.Equal(pyTriples, dotnetTriples);
    }

    /// <summary>
    /// Content-level stdout/exit-code parity for the unknown-<c>--persona</c> error case: Python's
    /// <c>resolve_target_personas</c> raises <c>SystemExit(message)</c> (lines 384-388), which exits
    /// 1 with <paramref name="message" /> on stderr; CliRunner.cs's own remarks document the
    /// equivalent InvalidOperationException-catch-and-exit-1 path. Compared on exit code and on
    /// both sides' stderr mentioning the offending id -- not on identical wording, since Python's
    /// message embeds a Python list repr and C#'s doesn't.
    /// </summary>
    [Fact]
    public async Task DryRun_MatchesRealPythonTwin_ForUnknownPersona_AtTheExitCodeAndStderrContentLevel()
    {
        const string bogusPersonaId = "totally-bogus-persona-id-zzz";
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var scriptPath = Path.Combine(repoRoot, "app", "backend", "setup_search_index.py");

        var interpreter = await PythonInterop.RequireInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        var (pyExitCode, _, pyStderr) = await PythonInterop.RunAsync(
            interpreter, [scriptPath, "--dry-run", "--persona", bogusPersonaId], TestContext.Current.CancellationToken);
        Assert.NotEqual(0, pyExitCode);
        Assert.Contains(bogusPersonaId, pyStderr);

        var dotnetStdout = new StringWriter();
        var dotnetStderr = new StringWriter();
        var dotnetExitCode = await CliRunner.RunAsync(
            ["--dry-run", "--persona", bogusPersonaId], dotnetStdout, dotnetStderr, repoRootOverride: repoRoot);
        Assert.NotEqual(0, dotnetExitCode);
        Assert.Contains(bogusPersonaId, dotnetStderr.ToString());

        // Same FAMILY of exit code (both "the process failed") -- Python's SystemExit(str) and
        // CliRunner's catch-and-return-1 both always use exactly 1, so this is also an exact match.
        Assert.Equal(pyExitCode, dotnetExitCode);
    }

    private static List<(string Persona, string Index, string Count)> ExtractDryRunTriples(string stdout) =>
        DryRunLine.Matches(stdout)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)
            .ToList();
}

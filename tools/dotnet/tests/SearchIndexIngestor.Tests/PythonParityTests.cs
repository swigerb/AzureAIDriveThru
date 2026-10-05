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
/// Issue #16 round 2 (Rick's review, item 1): the .NET side of the comparison now drives the REAL
/// <see cref="SearchIndexOrchestrator.IngestAsync"/> end to end -- including its own delete-stale
/// and $count steps -- instead of a hand-rolled create+upload loop standing in for it, so a
/// regression in IngestAsync's actual sequencing/wiring fails this test too. The delete-stale/
/// $count requests this now genuinely exercises are still NOT byte-compared against Python's own
/// output below -- the capture harness never drives those two Python-side calls (see
/// SearchIndexHttpClientTests.cs/SearchIndexOrchestratorTests.cs's own remarks for why: they're
/// about real index STATE/pagination, not a deterministic request-building concern a captured
/// fixture could usefully pin down) -- but a real pass through them (one seeded stale id actually
/// gets deleted, the count call actually gets made, against the SAME real persona data Python's
/// own capture used) is proven directly in the one test below that drives IngestAsync.
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
///   DocumentBatchBuilder.cs/SearchIndexOrchestrator.cs batch-size regression) -- the one enabled
///   persona with 180 documents (2 real batches of 100+80) had its first batch fail with
///   "array length differs. expected 100, actual 99."
/// * Reverted SearchIndexOrchestrator.cs's embedding-vector loop from
///   <c>JsonNode.Parse(PythonFloatRepr(component))</c> back to plain <c>vector.Add(component)</c>
///   -- this is how the "1" vs "1.0" int/float-shape divergence documented below was actually
///   *found* in the first place (not a check added after the fact): the fix is also covered by a
///   dedicated C#-only unit test, SearchIndexOrchestratorTests.cs's
///   <c>IngestAsync_WritesWholeNumberEmbeddingComponents_WithAPythonFaithfulDecimalPoint</c>, since
///   this parity test's own embedding-attachment loop (below) is independent of
///   SearchIndexOrchestrator.cs's and wouldn't itself catch a regression there.
/// * Round 2: removed the single seeded stale id from the FakeHttpMessageHandler's
///   search.post.search response -- the delete-request assertion correctly failed
///   (<c>Assert.Equal([staleDocId], deletedIds)</c> saw an empty list instead), confirming the new
///   delete-stale assertion actually exercises IngestAsync's delete path rather than vacuously
///   passing; restored afterwards.
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

            // Drives the REAL, unmodified SearchIndexOrchestrator.IngestAsync -- not a manual
            // create+upload loop reimplementing its own steps -- so a regression in its actual
            // ordering/wiring (not just a hand-rolled stand-in for it) would fail this test (Rick's
            // review, item 1/round 2: "drive the real SearchIndexOrchestrator in the parity test").
            // This also exercises the delete-stale and $count steps for real (not just create+
            // upload) -- deliberately still NOT byte-compared against Python's own output below
            // (the capture harness never drives those two calls -- see this file's own remarks,
            // and SearchIndexHttpClientTests.cs/SearchIndexOrchestratorTests.cs's own remarks, for
            // why: they're about real index STATE/pagination, not a deterministic request-building
            // concern a captured fixture could usefully pin down), but a real pass through them
            // (one seeded stale id actually gets deleted, the count call actually gets made) is
            // still proven here, against the SAME real persona data Python's own capture used.
            const string staleDocId = "stale-doc-left-over-from-a-prior-run";
            var handler = new FakeHttpMessageHandler(request =>
            {
                if (request.Method == HttpMethod.Put)
                {
                    return FakeHttpMessageHandler.Json(System.Net.HttpStatusCode.OK, request.Body!);
                }
                if (request.Uri.Contains("search.index"))
                {
                    var value = JsonNode.Parse(request.Body!)!["value"]!.AsArray();
                    var results = value.Select(d => new JsonObject { ["key"] = d!["id"]!.DeepClone(), ["status"] = true });
                    return FakeHttpMessageHandler.Json(
                        System.Net.HttpStatusCode.OK,
                        new JsonObject { ["value"] = new JsonArray(results.ToArray()) }.ToJsonString());
                }
                if (request.Uri.Contains("search.post.search"))
                {
                    // One synthetic stale id beyond the plan's own document ids -- proves
                    // IngestAsync's delete-stale step really runs (and really would delete it),
                    // not just the index-create/upload steps.
                    var ids = plan.Documents.Select(d => (JsonNode)new JsonObject { ["id"] = d["id"]!.DeepClone() }).ToList();
                    ids.Add(new JsonObject { ["id"] = staleDocId });
                    return FakeHttpMessageHandler.Json(
                        System.Net.HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(ids.ToArray()) }.ToJsonString());
                }
                return FakeHttpMessageHandler.PlainText(System.Net.HttpStatusCode.OK, plan.DocumentCount.ToString());
            });
            using var searchClient = new SearchIndexHttpClient(new HttpClient(handler), TestFixtureValues.FakeSearchEndpoint);
            var orchestrator = new SearchIndexOrchestrator(
                searchClient, new FixtureEmbeddingClient(), TestFixtureValues.FakeOpenAiEndpoint,
                TestFixtureValues.FakeEmbeddingDeployment, TextWriter.Null);

            await orchestrator.IngestAsync(plan, CancellationToken.None);

            var capturedIndexRequest = capturedPersona.GetProperty("index_request");
            var capturedUploadRequests = capturedPersona.GetProperty("upload_batch_requests");
            var uploadBatchCount = capturedUploadRequests.GetArrayLength();
            // 1 index-create-or-update request, plus one upload-batch request per 100-document
            // slice (same batch boundary on both sides -- DocumentBatchBuilder.cs's BatchSize),
            // plus the delete-stale listing request, the delete-batch request (one stale id
            // seeded above), and the final $count verification request.
            Assert.Equal(1 + uploadBatchCount + 3, handler.Requests.Count);

            AssertRequestMatches(capturedIndexRequest, handler.Requests[0], $"personas.{persona.PersonaId}.index_request");
            for (var i = 0; i < uploadBatchCount; i++)
            {
                AssertRequestMatches(
                    capturedUploadRequests[i], handler.Requests[i + 1], $"personas.{persona.PersonaId}.upload_batch_requests[{i}]");
            }

            // The three C#-only (not Python-captured) tail requests, in IngestAsync's own order:
            // list existing ids -> delete the one stale id found -> verify the final count.
            var listRequest = handler.Requests[uploadBatchCount + 1];
            var deleteRequest = handler.Requests[uploadBatchCount + 2];
            var countRequest = handler.Requests[uploadBatchCount + 3];
            Assert.Contains("search.post.search", listRequest.Uri);
            Assert.Contains("search.index", deleteRequest.Uri);
            var deleteBody = JsonNode.Parse(deleteRequest.Body!)!.AsObject();
            var deletedIds = deleteBody["value"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()).ToList();
            Assert.Equal([staleDocId], deletedIds);
            Assert.Equal("delete", deleteBody["value"]!.AsArray()[0]!["@search.action"]!.GetValue<string>());
            Assert.Contains("$count", countRequest.Uri);
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
    /// Same content-level dry-run comparison as the "every enabled persona" test above, but for a
    /// single targeted persona via <c>--persona</c> (Rick's review, round 2, item 5: "Add
    /// Python-vs-C# tests for a single persona"). Proves <c>resolve_target_personas</c>'s
    /// single-id-filter path -- not just the "no --persona at all" default path the other test
    /// covers -- stays in lockstep between the two sides. Picks whichever persona id happens to
    /// sort first out of the real repo's own enabled catalog at run time (never a literal brand
    /// name baked into this source file -- this repo's own rebrand-word ratchet test would flag a
    /// hardcoded persona id the same way it flags any other unlisted brand word).
    /// </summary>
    [Fact]
    public async Task DryRun_MatchesRealPythonTwin_ForASinglePersona_AtTheStdoutContentLevel()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var scriptPath = Path.Combine(repoRoot, "app", "backend", "setup_search_index.py");
        var personaId = EnabledPersonaDiscovery.DiscoverAll(repoRoot)
            .Select(p => p.PersonaId).OrderBy(id => id, StringComparer.Ordinal).First();

        var interpreter = await PythonInterop.RequireInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        var (pyExitCode, pyStdout, pyStderr) = await PythonInterop.RunAsync(
            interpreter, [scriptPath, "--dry-run", "--persona", personaId], TestContext.Current.CancellationToken,
            extraEnvironment: new Dictionary<string, string> { ["COLUMNS"] = "1000" });
        Assert.True(pyExitCode == 0, $"Python --dry-run --persona {personaId} exited {pyExitCode}.\nstdout:\n{pyStdout}\nstderr:\n{pyStderr}");

        var dotnetStdout = new StringWriter();
        var dotnetStderr = new StringWriter();
        var dotnetExitCode = await CliRunner.RunAsync(
            ["--dry-run", "--persona", personaId], dotnetStdout, dotnetStderr, repoRootOverride: repoRoot);
        Assert.Equal(0, dotnetExitCode);
        Assert.Equal("", dotnetStderr.ToString());

        var pyTriples = ExtractDryRunTriples(pyStdout);
        var dotnetTriples = ExtractDryRunTriples(dotnetStdout.ToString());
        Assert.Equal([personaId], pyTriples.Select(t => t.Persona));
        Assert.Equal(pyTriples, dotnetTriples);
    }

    /// <summary>
    /// Content-level dry-run parity for the <c>PERSONAS</c> env var restricting the catalog to a
    /// named subset (Rick's review, round 2, item 5: "...and PERSONAS"). Python's own
    /// <c>PersonaCatalog.load()</c> reads <c>PERSONAS</c> as a comma list (persona_loader.py, line
    /// 573); <see cref="PersonaCatalogEnvResolver.ResolveEnabledIds"/> is this port's equivalent.
    /// Uses a real-repo subset that deliberately excludes at least one enabled persona (not "every
    /// enabled persona", which the other dry-run test above already covers) so the restriction is
    /// actually observable, not vacuously "the same as the unfiltered case" -- picked at run time
    /// from the real catalog, never a literal brand name baked into this source file (same
    /// rebrand-word-ratchet reasoning as the single-persona test above).
    /// </summary>
    [Fact]
    public async Task DryRun_MatchesRealPythonTwin_ForPersonasEnvironmentVariable_AtTheStdoutContentLevel()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var scriptPath = Path.Combine(repoRoot, "app", "backend", "setup_search_index.py");
        var allPersonaIds = EnabledPersonaDiscovery.DiscoverAll(repoRoot)
            .Select(p => p.PersonaId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.True(allPersonaIds.Count >= 2, "Expected at least two enabled personas in the real repo to restrict between.");
        var restrictedIds = allPersonaIds.Take(allPersonaIds.Count - 1).ToList();
        var personasValue = string.Join(",", restrictedIds);

        var interpreter = await PythonInterop.RequireInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        var (pyExitCode, pyStdout, pyStderr) = await PythonInterop.RunAsync(
            interpreter, [scriptPath, "--dry-run"], TestContext.Current.CancellationToken,
            extraEnvironment: new Dictionary<string, string> { ["COLUMNS"] = "1000", ["PERSONAS"] = personasValue });
        Assert.True(pyExitCode == 0, $"Python --dry-run PERSONAS={personasValue} exited {pyExitCode}.\nstdout:\n{pyStdout}\nstderr:\n{pyStderr}");

        var dotnetStdout = new StringWriter();
        var dotnetStderr = new StringWriter();
        var dotnetExitCode = await CliRunner.RunAsync(
            ["--dry-run"], dotnetStdout, dotnetStderr, repoRootOverride: repoRoot,
            getEnvironmentVariable: name => name == "PERSONAS" ? personasValue : null);
        Assert.Equal(0, dotnetExitCode);
        Assert.Equal("", dotnetStderr.ToString());

        var pyTriples = ExtractDryRunTriples(pyStdout);
        var dotnetTriples = ExtractDryRunTriples(dotnetStdout.ToString());
        Assert.Equal(restrictedIds, pyTriples.Select(t => t.Persona).OrderBy(id => id, StringComparer.Ordinal));
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

    [Fact]
    public async Task NonDryRun_MatchesRealPythonTwin_WhenAzureSearchEndpointIsMissing_AtTheExitCodeAndStderrContentLevel()
    {
        // Round 2, item 5 ("...both missing-endpoint errors..."). Python's own equivalent
        // (run()'s plain os.environ["AZURE_SEARCH_ENDPOINT"], line 471) only runs for a non-dry-run
        // invocation, and main() always calls load_azd_env() first for a non-dry-run invocation --
        // which shells out to the real `azd` CLI (line 95-96: subprocess.run("azd env list -o
        // json", shell=True)). `azd` itself is not installed in this (or presumably any CI) test
        // sandbox, so a literal non-dry-run subprocess run would otherwise always fail at that azd
        // shell-out ("Error loading azd env") before ever reaching the endpoint lookup this test
        // means to compare -- not a useful parity signal. A tiny stub `azd` script prepended to
        // PATH for this one process (CreateStubAzdOnPath below) lets load_azd_env() succeed
        // harmlessly against an azd-environment file that deliberately does NOT set
        // AZURE_SEARCH_ENDPOINT, so Python actually reaches line 471's KeyError.
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var scriptPath = Path.Combine(repoRoot, "app", "backend", "setup_search_index.py");

        var interpreter = await PythonInterop.RequireInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        var stubAzdDir = CreateStubAzdOnPath(skipIndexSetup: false, setEndpoints: false);
        var extraEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = stubAzdDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        };
        var (pyExitCode, _, pyStderr) = await PythonInterop.RunAsync(
            interpreter,
            [scriptPath],
            TestContext.Current.CancellationToken,
            extraEnvironment);
        Assert.NotEqual(0, pyExitCode);
        Assert.Contains("AZURE_SEARCH_ENDPOINT", pyStderr);

        var dotnetStdout = new StringWriter();
        var dotnetStderr = new StringWriter();
        var dotnetExitCode = await CliRunner.RunAsync(
            [],
            dotnetStdout,
            dotnetStderr,
            repoRootOverride: repoRoot,
            getEnvironmentVariable: _ => null,
            loadAzdEnvValues: _ => new Dictionary<string, string>());
        Assert.NotEqual(0, dotnetExitCode);
        Assert.Contains("AZURE_SEARCH_ENDPOINT", dotnetStderr.ToString());

        // Both sides mention the SAME missing variable and both fail with exit code 1 -- Python's
        // raw, unhandled KeyError traceback vs this port's own clean, single-line message are a
        // deliberate, documented divergence (see SearchEndpointResolver.cs's own remarks), not a
        // byte-for-byte match, so content-level ("mentions the right variable, same exit family")
        // is the right level of comparison here, same as the unknown-persona test above.
        Assert.Equal(pyExitCode, dotnetExitCode);
    }

    [Fact]
    public async Task NonDryRun_MatchesRealPythonTwin_WhenAzureOpenAiEndpointIsMissing_AtTheExitCodeAndStderrContentLevel()
    {
        // Same reasoning as the AZURE_SEARCH_ENDPOINT test above, but for run()'s very next line
        // (472: os.environ["AZURE_OPENAI_EASTUS2_ENDPOINT"]) -- AZURE_SEARCH_ENDPOINT must be
        // supplied so both sides get past that first check and actually reach this one.
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var scriptPath = Path.Combine(repoRoot, "app", "backend", "setup_search_index.py");

        var interpreter = await PythonInterop.RequireInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        var stubAzdDir = CreateStubAzdOnPath(skipIndexSetup: false, setEndpoints: false, setSearchEndpointOnly: true);
        var extraEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = stubAzdDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        };
        var (pyExitCode, _, pyStderr) = await PythonInterop.RunAsync(
            interpreter,
            [scriptPath],
            TestContext.Current.CancellationToken,
            extraEnvironment);
        Assert.NotEqual(0, pyExitCode);
        Assert.Contains("AZURE_OPENAI_EASTUS2_ENDPOINT", pyStderr);

        var dotnetStdout = new StringWriter();
        var dotnetStderr = new StringWriter();
        var dotnetExitCode = await CliRunner.RunAsync(
            ["--search-endpoint", "https://fake.search.windows.net"],
            dotnetStdout,
            dotnetStderr,
            repoRootOverride: repoRoot,
            getEnvironmentVariable: _ => null,
            loadAzdEnvValues: _ => new Dictionary<string, string>());
        Assert.NotEqual(0, dotnetExitCode);
        Assert.Contains("AZURE_OPENAI_EASTUS2_ENDPOINT", dotnetStderr.ToString());
        Assert.Equal(pyExitCode, dotnetExitCode);
    }

    /// <summary>
    /// Writes a tiny stub `azd` script (just enough to satisfy load_azd_env()'s own
    /// <c>subprocess.run("azd env list -o json", shell=True)</c> plus <c>load_dotenv(path,
    /// override=True)</c>) to a fresh temp directory and returns that directory, for prepending to
    /// PATH. Never talks to a real azd installation or Azure.
    /// </summary>
    private static string CreateStubAzdOnPath(
        bool skipIndexSetup, bool setEndpoints, bool setSearchEndpointOnly = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "stub-azd-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var envFile = Path.Combine(dir, "env.env");
        var lines = new List<string>();
        if (setEndpoints || setSearchEndpointOnly)
        {
            lines.Add("AZURE_SEARCH_ENDPOINT=https://fake.search.windows.net");
        }
        if (setEndpoints)
        {
            lines.Add("AZURE_OPENAI_EASTUS2_ENDPOINT=https://fake.openai.azure.com");
        }
        if (skipIndexSetup)
        {
            lines.Add("AZURE_SEARCH_SKIP_INDEX_SETUP=true");
        }
        File.WriteAllLines(envFile, lines);

        var script = Path.Combine(dir, "azd");
        File.WriteAllText(
            script,
            $$"""
            #!/bin/sh
            echo '[{"Name":"test","IsDefault":true,"DotEnvPath":"{{envFile}}"}]'
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return dir;
    }

    private static List<(string Persona, string Index, string Count)> ExtractDryRunTriples(string stdout) =>
        DryRunLine.Matches(stdout)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)
            .ToList();
}

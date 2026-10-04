using System.Text.Json;
using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Issue #16's acceptance bar applied to an Azure-dependent tool via the "client-seam +
/// recorded-fixture" design note (docs/dotnet_tooling.md): there is no live Azure/OpenAI call on
/// either side of this comparison, ever. The REAL, unmodified app/backend/setup_search_index.py
/// is driven as a genuine subprocess (Fixtures/capture_search_index_requests.py, itself only a
/// thin HTTP-transport-capture harness around the twin's own real, read-only
/// build_plan/create_or_update_index/upload_documents functions -- see that file's own docstring),
/// and its captured request bodies are compared structurally (see JsonStructuralAssert) against
/// SearchIndexRequestPlanner's independent C# reconstruction of the same bodies, for every
/// enabled persona in the real repo (today: three personas, each discovered from its own
/// personas/*/persona.json folder -- never hard-coded here).
///
/// Mutation check performed while implementing this port (each reverted immediately after
/// confirming):
/// * Changing DocumentBatchBuilder.cs's BatchSize from 100 to 99 made this test fail (the largest
///   real persona's 180-document export produces [99, 81] batches here instead of Azure Search's
///   real [100, 80] boundary) -- confirmed, then restored. (See DocumentBatchBuilderTests.cs for
///   the dedicated, synthetic-fixture coverage of this same boundary.)
/// * Changing FixtureEmbedding.cs's byte-to-[-1,1] divisor from 255.0 to 256.0 made this test fail
///   on every persona's embedding values -- confirmed, then restored. (PythonJsonDumps.cs's
///   ensure_ascii escaping and FixtureEmbedding.cs's F6-string-round-trip rounding are each
///   guarded by a DEDICATED unit test instead -- see PythonJsonDumpsTests.cs/
///   FixtureEmbeddingTests.cs's own mutation-check remarks -- because today's real persona data
///   never contains a non-ASCII "sizes" value, and the F6-round-trip vs. raw Math.Round(x, 6)
///   distinction turned out to produce bit-identical results for every one of this formula's 256
///   possible input bytes, so reverting either one leaves THIS real-data test green.)
/// * Temporarily making TestFixtureValues.FakeOpenAiEndpoint differ from the harness's own
///   FAKE_OPENAI_ENDPOINT made this test fail on the index_definition's vectorizer resourceUri --
///   confirmed, then restored.
/// </summary>
public sealed class PythonParityTests
{
    [Fact]
    public async Task DotnetPort_MatchesRealPythonTwin_ForEveryEnabledPersona()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var harnessScript = Path.Combine(
            repoRoot, "tools", "dotnet", "tests", "SearchIndexRequestBuilder.Tests", "Fixtures",
            "capture_search_index_requests.py");
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

        // The harness discovers personas the exact same way (every personas/*/persona.json,
        // sorted) as EnabledPersonaDiscovery itself -- this just confirms neither side silently
        // skipped a persona the other one found, before comparing their actual request bodies.
        var dotnetPersonas = EnabledPersonaDiscovery.DiscoverAll(repoRoot);
        var capturedPersonaIds = capturedPersonas.EnumerateObject().Select(p => p.Name).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var dotnetPersonaIds = dotnetPersonas.Select(p => p.PersonaId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(capturedPersonaIds, dotnetPersonaIds);
        Assert.True(dotnetPersonaIds.Count > 0, "Expected at least one enabled persona in the real repo.");

        foreach (var persona in dotnetPersonas)
        {
            var plan = SearchIndexRequestPlanner.BuildPlan(
                persona,
                TestFixtureValues.FakeOpenAiEndpoint,
                TestFixtureValues.FakeEmbeddingDeployment,
                FixtureEmbedding.For);
            var capturedPersona = capturedPersonas.GetProperty(persona.PersonaId);

            Assert.Equal(capturedPersona.GetProperty("index_name").GetString(), plan.IndexName);
            Assert.Equal(capturedPersona.GetProperty("document_count").GetInt32(), plan.DocumentCount);

            var capturedIndexDefinition = JsonNode.Parse(capturedPersona.GetProperty("index_definition").GetRawText());
            JsonStructuralAssert.Equal(
                capturedIndexDefinition, plan.IndexDefinition, $"personas.{persona.PersonaId}.index_definition");

            var capturedBatches = capturedPersona.GetProperty("document_batches");
            Assert.Equal(capturedBatches.GetArrayLength(), plan.DocumentBatches.Count);
            for (var i = 0; i < plan.DocumentBatches.Count; i++)
            {
                var capturedBatchValue = JsonNode.Parse(capturedBatches[i].GetRawText());
                var dotnetBatchValue = plan.DocumentBatches[i]["value"];
                JsonStructuralAssert.Equal(
                    capturedBatchValue, dotnetBatchValue, $"personas.{persona.PersonaId}.document_batches[{i}]");
            }
        }
    }
}

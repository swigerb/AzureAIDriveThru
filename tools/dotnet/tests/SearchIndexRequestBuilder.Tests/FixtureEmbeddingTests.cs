namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Unit tests for FixtureEmbedding's determinism/shape, plus a cross-language check against the
/// capture harness's own fixture_embedding() (Fixtures/capture_search_index_requests.py) for a
/// handful of sample strings -- belt-and-suspenders on top of
/// PythonParityTests.DotnetPort_MatchesRealPythonTwin_ForEveryEnabledPersona, which only exercises
/// this formula indirectly (via whatever combined_text strings happen to exist in real menu data
/// today).
/// </summary>
public sealed class FixtureEmbeddingTests
{
    [Fact]
    public void For_IsDeterministic_ForTheSameText()
    {
        var first = FixtureEmbedding.For("Burgers Classic Cheeseburger A juicy burger with cheese");
        var second = FixtureEmbedding.For("Burgers Classic Cheeseburger A juicy burger with cheese");
        Assert.Equal(first, second);
    }

    [Fact]
    public void For_ReturnsEightComponents_EachWithinMinusOneToOne()
    {
        var values = FixtureEmbedding.For("anything");
        Assert.Equal(8, values.Count);
        Assert.All(values, v => Assert.InRange(v, -1.0, 1.0));
    }

    [Fact]
    public void For_DiffersForDifferentText()
    {
        var a = FixtureEmbedding.For("Burgers Classic Cheeseburger");
        var b = FixtureEmbedding.For("Drinks Classic Vanilla Shake");
        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Mutation check performed while implementing this test (reverted immediately after
    /// confirming): changing the byte-to-[-1,1] divisor in FixtureEmbedding.cs from 255.0 to 256.0
    /// made this test FAIL for all three sample texts below AND made
    /// PythonParityTests.DotnetPort_MatchesRealPythonTwin_ForEveryEnabledPersona fail on real
    /// persona data too (every document's "embedding" field differs) -- confirming both tests
    /// genuinely guard this formula, restored before committing. (Separately verified, and NOT a
    /// real guard: swapping the F6-string-round-trip rounding for a raw <c>Math.Round(raw, 6)</c>
    /// call produces bit-identical results for every possible input byte 0-255 for THIS SPECIFIC
    /// formula -- so that particular implementation detail has no test coverage distinguishing it,
    /// and isn't claimed as a mutation-check finding here.)
    /// </summary>
    [Theory]
    [InlineData("Burgers Classic Cheeseburger A juicy burger with cheese")]
    [InlineData("Drinks Classic Vanilla Shake A thick, creamy shake")]
    [InlineData("café 🎉")]
    public async Task For_MatchesRealPythonCaptureHarnessFixtureEmbedding(string text)
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var interpreter = await PythonInterop.RequireInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        var harnessScript = Path.Combine(
            repoRoot, "tools", "dotnet", "tests", "SearchIndexRequestBuilder.Tests", "Fixtures",
            "capture_search_index_requests.py");

        const string probe =
            "import importlib.util, json, sys\n" +
            "spec = importlib.util.spec_from_file_location('capture_harness', sys.argv[1])\n" +
            "module = importlib.util.module_from_spec(spec)\n" +
            "spec.loader.exec_module(module)\n" +
            "print(json.dumps(module.fixture_embedding(sys.argv[2])))\n";

        var (exitCode, stdout, stderr) = await PythonInterop.RunAsync(
            interpreter, ["-c", probe, harnessScript, text], TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Python oracle exited {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        var expected = System.Text.Json.JsonSerializer.Deserialize<double[]>(stdout.Trim())!;
        var actual = FixtureEmbedding.For(text);
        Assert.Equal(expected, actual);
    }
}

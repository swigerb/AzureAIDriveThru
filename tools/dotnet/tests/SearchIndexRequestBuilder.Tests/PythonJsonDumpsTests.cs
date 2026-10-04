using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Cross-checks SearchIndexRequestBuilder.PythonJsonDumps.Serialize against a REAL Python
/// <c>json.dumps(value)</c> (default arguments) subprocess oracle, for synthetic inputs chosen to
/// exercise exactly the behaviours real menu "sizes" data is too simple to ever exercise (per
/// docs/dotnet_tooling.md: real sizes fields observed in the repo today are plain size/price pairs,
/// never non-ASCII) -- without this, the mutation below would pass the real-data parity test
/// undetected.
///
/// Mutation check performed while implementing this test (reverted immediately after confirming):
/// commenting out the "\uXXXX" escape branch in PythonJsonDumps.WriteString (falling back to
/// appending the raw character instead) left
/// PythonParityTests.DotnetPort_MatchesRealPythonTwin_ForEveryEnabledPersona GREEN (today's real
/// persona data never puts a non-ASCII character in a "sizes" value), but made
/// Serialize_EscapesNonAsciiAndAstralCharacters_LikeRealPythonJsonDumps below FAIL immediately
/// (mismatched escaped vs. raw-emoji output) -- confirming this synthetic test is the one actually
/// guarding that behaviour, restored before committing.
/// </summary>
public sealed class PythonJsonDumpsTests
{
    [Theory]
    [InlineData("""{"size":"large","price":2.5}""")]
    [InlineData("""{"size":"large","price":2}""")]
    [InlineData("""{"size":"large","price":1.50}""")]
    [InlineData("""[1, 2.0, 3.25, -0.5, 100]""")]
    [InlineData("""{"note":"quote\" backslash\\ tab\there"}""")]
    [InlineData("""{"nested":{"a":[1,2,{"b":null,"c":true,"d":false}]}}""")]
    public async Task Serialize_MatchesRealPythonJsonDumps_ForAsciiOnlyInputs(string json)
    {
        await AssertMatchesPythonJsonDumpsAsync(json);
    }

    /// <summary>The one case real menu data never exercises (see type-level remarks): a string
    /// value containing a non-ASCII character AND an astral (surrogate-pair) emoji, which Python's
    /// default ensure_ascii=True escapes per UTF-16 code unit -- exactly what this test's
    /// mutation-check targets.</summary>
    [Fact]
    public async Task Serialize_EscapesNonAsciiAndAstralCharacters_LikeRealPythonJsonDumps()
    {
        // café (é = U+00E9, a single UTF-16 code unit) + 🎉 (U+1F389, an astral character encoded
        // as a UTF-16 surrogate pair) + NBSP (U+00A0).
        const string json = """{"label":"café\u00a0🎉"}""";
        await AssertMatchesPythonJsonDumpsAsync(json);
    }

    private static async Task AssertMatchesPythonJsonDumpsAsync(string json)
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var interpreter = await PythonInterop.RequireInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        const string probe = "import json, sys\nprint(json.dumps(json.loads(sys.argv[1])))\n";
        var (exitCode, pythonOutput, stderr) = await PythonInterop.RunAsync(
            interpreter, ["-c", probe, json], TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Python oracle exited {exitCode}.\nstderr:\n{stderr}");

        // Python's print() appends a trailing newline this harness must strip before comparing.
        var expected = pythonOutput.TrimEnd('\r', '\n');

        var node = JsonNode.Parse(json);
        var actual = PythonJsonDumps.Serialize(node);

        Assert.Equal(expected, actual);
    }
}

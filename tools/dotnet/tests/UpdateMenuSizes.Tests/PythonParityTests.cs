using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UpdateMenuSizes;

namespace UpdateMenuSizes.Tests;

/// <summary>
/// Issue #16's acceptance bar for a ported tool: "the C# tool produces the same output as its
/// Python twin." This drives the REAL scripts/update_menu_sizes.py as a genuine subprocess
/// against a private copy of the real persona fixtures (discovered via
/// <see cref="PersonaMenuLocator"/>, not a hardcoded persona-id literal -- PR #224 review R1), drives
/// <see cref="MenuSizeUpdater.UpdateMenu"/> in-process against a second private copy of the same
/// fixtures, then asserts (PR #224 review R2/R3):
///   1. the two resulting menuItems.json files are BYTE-IDENTICAL (not just structurally equal --
///      json.dump's ensure_ascii=False escaping, Python's per-OS newline translation, and float
///      repr formatting are all externally observable and must match exactly, not just the parsed
///      values);
///   2. the two programs' captured console output is identical, line for line including the
///      blank-line-then-summary and the exact "['mini', 'small']"-style Python list repr; and
///   3. the "Updated N items" count the two programs report is identical.
///
/// Mutation check (see MenuSizeUpdaterTests for the individual-behavior unit tests with their own
/// mutation-check notes): reverting PythonJsonEncoder to JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
/// reverting the trailing-newline literal back to "\n", or reverting PythonFloatRepr's call site
/// back to assigning the raw decimal, each independently makes the byte-identical assertion below
/// fail against the real fixtures (confirmed while implementing this fix).
///
/// Never touches the real, checked-in personas/&lt;id&gt;/menu/** files -- both runs operate on
/// throwaway copies under a per-test temp directory, matching this repo's existing C# test
/// convention (see e.g. app/backend-dotnet/tests/Backend.Tests/TestSupport/PersonaPackFixture.cs,
/// tests/conformance/tests/Conformance.Tests/MenuIndexResolveIndexPathsTests.cs).
/// </summary>
public sealed class PythonParityTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "squanchy-update-menu-sizes-parity-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DotnetPort_ProducesByteIdenticalOutput_ToRealPythonScript()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var discovery = PersonaMenuLocator.Locate(repoRoot);
        var realScript = Path.Combine(repoRoot, "scripts", "update_menu_sizes.py");

        Assert.True(File.Exists(discovery.ProductionFilePath), $"Fixture not found: {discovery.ProductionFilePath}");
        Assert.True(File.Exists(discovery.MenuFilePath), $"Fixture not found: {discovery.MenuFilePath}");
        Assert.True(File.Exists(discovery.ProductSearchMapFilePath), $"Fixture not found: {discovery.ProductSearchMapFilePath}");
        Assert.True(File.Exists(realScript), $"Python twin not found: {realScript}");

        var interpreter = await RequirePythonInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        // Lay out a private "repo" under _tempRoot containing only what update_menu_sizes.py's
        // __file__-relative paths need: scripts/update_menu_sizes.py, and a copy of the real
        // production export + menu file for the Python run to mutate. The Python twin (kept
        // untouched per issue #16's scope) hardcodes "personas/<a specific persona id>/menu/..."
        // in its own source, not whatever persona PersonaMenuLocator discovers -- so this layout
        // intentionally uses discovery.PersonaId (today, the one persona pack checked into the
        // repo) rather than a literal, but if that persona pack is ever renamed or a second one is
        // added, the Python run itself would need updating first (out of scope for this C# port).
        var pythonScriptsDir = Path.Combine(_tempRoot, "python-run", "scripts");
        var pythonMenuDir = Path.Combine(_tempRoot, "python-run", "personas", discovery.PersonaId, "menu");
        Directory.CreateDirectory(pythonScriptsDir);
        Directory.CreateDirectory(Path.Combine(pythonMenuDir, "source"));
        File.Copy(realScript, Path.Combine(pythonScriptsDir, "update_menu_sizes.py"));
        File.Copy(discovery.ProductionFilePath, Path.Combine(pythonMenuDir, "source", Path.GetFileName(discovery.ProductionFilePath)));
        var pythonMenuPath = Path.Combine(pythonMenuDir, "menuItems.json");
        File.Copy(discovery.MenuFilePath, pythonMenuPath);

        var (exitCode, pythonStdout, stderr) = await RunProcessAsync(
            interpreter, [Path.Combine(pythonScriptsDir, "update_menu_sizes.py")], TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Python twin exited {exitCode}.\nstdout:\n{pythonStdout}\nstderr:\n{stderr}");

        // Same two input fixtures (plus the product search map, needed by the 3-arg UpdateMenu --
        // PR #224 review R1), run through the C# port in-process instead.
        var dotnetDir = Path.Combine(_tempRoot, "dotnet-run");
        Directory.CreateDirectory(dotnetDir);
        var dotnetProductionPath = Path.Combine(dotnetDir, Path.GetFileName(discovery.ProductionFilePath));
        File.Copy(discovery.ProductionFilePath, dotnetProductionPath);
        var dotnetMenuPath = Path.Combine(dotnetDir, "menuItems.json");
        File.Copy(discovery.MenuFilePath, dotnetMenuPath);
        var dotnetProductSearchMapPath = Path.Combine(dotnetDir, "product_search_map.json");
        File.Copy(discovery.ProductSearchMapFilePath, dotnetProductSearchMapPath);

        var result = MenuSizeUpdater.UpdateMenu(dotnetProductionPath, dotnetMenuPath, dotnetProductSearchMapPath);

        // 1. Byte-identical file output -- not structural equality. Proves PythonJsonEncoder's
        // ensure_ascii=False escaping, the Environment.NewLine-based newline handling (including
        // the final trailing newline), and PythonFloatRepr's trailing-zero-dropping all match the
        // real Python twin against the real fixtures, not just a synthetic unit-test sample.
        var pythonBytes = File.ReadAllBytes(pythonMenuPath);
        var dotnetBytes = File.ReadAllBytes(dotnetMenuPath);
        Assert.True(
            pythonBytes.AsSpan().SequenceEqual(dotnetBytes),
            "The C# port's menuItems.json output is not byte-identical to the Python twin's output " +
            $"(python={pythonBytes.Length} bytes, dotnet={dotnetBytes.Length} bytes).");

        // 2. Console output: reconstruct what Program.cs would have printed (one Console.WriteLine
        // per Log entry, each terminated by Environment.NewLine, matching how Python's print()
        // terminates every line including the final one) and compare it to Python's own captured
        // stdout verbatim -- this also exercises the per-item SKIP/UPDATED line text (including the
        // Python-list-repr "['mini', 'small']" sizes format) and the blank-line-then-summary shape.
        var dotnetStdout = string.Join(Environment.NewLine, result.Log) + Environment.NewLine;
        Assert.Equal(pythonStdout, dotnetStdout);

        // 3. Updated-item count: parsed from each program's own "Updated N items..." stdout line,
        // not just read off UpdateResult.UpdatedCount directly -- this is what a human (or CI log
        // reader) actually sees, and is a second, independent check on top of the raw byte/line
        // comparisons above.
        var pythonUpdatedCount = ParseUpdatedCount(pythonStdout);
        var dotnetUpdatedCount = ParseUpdatedCount(dotnetStdout);
        Assert.Equal(pythonUpdatedCount, dotnetUpdatedCount);
        Assert.Equal(pythonUpdatedCount, result.UpdatedCount);
    }

    /// <summary>
    /// PR #224 review R3: there are now two sources of truth for the menuItems.json-name -&gt;
    /// production-search-term map -- the Python script's own hardcoded <c>PRODUCT_SEARCH_MAP</c>
    /// dict (kept untouched, per issue #16's scope) and the externalized
    /// <c>personas/&lt;id&gt;/menu/product_search_map.json</c> this C# port reads instead
    /// (<see cref="MenuSizeUpdater.LoadProductSearchMap"/>). Three of the six real entries never
    /// surface in <c>UpdateMenu</c>'s own log/output (their production search terms don't match
    /// anything in today's fixture, so they're silently absorbed into a "no production data found"
    /// SKIP line that says nothing about the search term itself) -- so the byte-identical parity
    /// test above cannot, by itself, catch the two files drifting apart. This test instead loads
    /// the Python script's module-level <c>PRODUCT_SEARCH_MAP</c> directly (via <c>runpy</c>,
    /// without running <c>update_menu()</c>) and asserts it matches the JSON file exactly --
    /// same keys, same values, same ORDER (Python 3.7+ dict order, and <c>json.dumps</c>'s default
    /// order, are both insertion order, so this also catches an entry reordered relative to the
    /// twin, not just a value or key changed).
    /// </summary>
    [Fact]
    public async Task ProductSearchMapFile_MatchesPythonScriptsHardcodedDict_ExactlyInOrder()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var discovery = PersonaMenuLocator.Locate(repoRoot);
        var realScript = Path.Combine(repoRoot, "scripts", "update_menu_sizes.py");

        Assert.True(File.Exists(realScript), $"Python twin not found: {realScript}");
        Assert.True(File.Exists(discovery.ProductSearchMapFilePath), $"Fixture not found: {discovery.ProductSearchMapFilePath}");

        var interpreter = await RequirePythonInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        // runpy.run_path's default run_name is "<run_path>", not "__main__", so the script's own
        // `if __name__ == "__main__": update_menu()` guard never fires -- only the module-level
        // constants (including PRODUCT_SEARCH_MAP) are evaluated, with no file I/O against the
        // real production/menu fixtures.
        const string inspectProductSearchMap =
            "import json, runpy, sys\n" +
            "ns = runpy.run_path(sys.argv[1])\n" +
            "print(json.dumps(ns['PRODUCT_SEARCH_MAP']))\n";
        var (exitCode, pythonStdout, stderr) = await RunProcessAsync(
            interpreter, ["-c", inspectProductSearchMap, realScript], TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Python inspection exited {exitCode}.\nstdout:\n{pythonStdout}\nstderr:\n{stderr}");

        using var pythonDoc = JsonDocument.Parse(pythonStdout.Trim());
        using var fileDoc = JsonDocument.Parse(
            await File.ReadAllTextAsync(discovery.ProductSearchMapFilePath, TestContext.Current.CancellationToken));

        var pythonEntries = pythonDoc.RootElement.EnumerateObject()
            .Select(p => (Key: p.Name, Value: p.Value.GetString()))
            .ToList();
        var fileEntries = fileDoc.RootElement.EnumerateObject()
            .Select(p => (Key: p.Name, Value: p.Value.GetString()))
            .ToList();

        Assert.Equal(pythonEntries, fileEntries);
    }

    private static int ParseUpdatedCount(string stdout)
    {
        var match = Regex.Match(stdout, @"Updated (\d+) items in menuItems\.json");
        Assert.True(match.Success, $"Could not find an 'Updated N items...' line in:\n{stdout}");
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>
    /// Resolves a working Python interpreter via <see cref="FindWorkingPythonInterpreterAsync"/>,
    /// or fails/skips the calling test the same way both parity tests in this class need to: a
    /// missing interpreter is a legitimate local-dev skip, but a hard failure in CI (update_menu_sizes.py
    /// and this inspection script both need only the standard library, so CI must always have one).
    /// </summary>
    private static async Task<string?> RequirePythonInterpreterAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var interpreter = await FindWorkingPythonInterpreterAsync(repoRoot, cancellationToken);
        if (interpreter is not null)
        {
            return interpreter;
        }

        var isCi = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        var message =
            "No Python interpreter was found (checked the repo-root .venv, then bare " +
            "\"python3\"/\"python\" on PATH). This test needs only the standard library " +
            "(json, pathlib, runpy), so any Python 3 interpreter works.";
        if (isCi)
        {
            Assert.Fail(message + " CI must have a Python interpreter available -- this is not a legitimate skip in CI.");
        }
        Assert.Skip(message + " Install Python 3, or run `python -m venv .venv` at the repo root, to run this test locally.");
        return null;
    }

    /// <summary>
    /// Candidate interpreters in preference order: the repo-root .venv (same convention as
    /// tests/conformance/src/Conformance.Harness/RepoPaths.cs::PythonExecutable), then a bare
    /// "python3"/"python" on PATH for a developer machine without one. update_menu_sizes.py needs
    /// only the standard library, so the FIRST interpreter that runs at all (not specifically one
    /// with any particular package importable) qualifies.
    /// </summary>
    private static async Task<string?> FindWorkingPythonInterpreterAsync(string repoRoot, CancellationToken cancellationToken)
    {
        string[] candidates =
        [
            OperatingSystem.IsWindows()
                ? Path.Combine(repoRoot, ".venv", "Scripts", "python.exe")
                : Path.Combine(repoRoot, ".venv", "bin", "python"),
            "python3",
            "python",
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                var (exitCode, _, _) = await RunProcessAsync(candidate, ["--version"], cancellationToken);
                if (exitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
            {
                // Not found on this machine/PATH -- try the next candidate.
            }
        }
        return null;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Without this, .NET decodes the child's redirected stdout using the console's
            // legacy codepage (not UTF-8), mangling the non-ASCII characters this very test is
            // trying to prove survive round-tripping unescaped (e.g. (R) in "Ocean Water(R)").
            // PYTHONIOENCODING forces Python's side of the pipe to also encode as UTF-8,
            // regardless of the host console's codepage.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}

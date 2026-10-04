using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExtractProductionItems.Tests;

/// <summary>
/// Issue #16's acceptance bar for a ported tool: "the C# tool produces the same output as its
/// Python twin." This drives the REAL scripts/extract_production_items.py as a genuine subprocess
/// -- unmodified, at its real repo location -- drives <see cref="ProductionItemsExtractor"/>'s
/// equivalent pipeline in-process against the SAME two fixture files, then asserts the two
/// programs' captured stdout is identical byte-for-byte -- not just structurally equal. Unlike
/// update_menu_sizes.py, this tool is read-only (a report, no file mutation), so there is no
/// output-file comparison here, only stdout.
///
/// PR #243 review R1: this test previously resolved its fixtures via
/// <see cref="ProductionExportLocator.Locate"/> -- the same data-driven discovery this tool's own
/// CLI uses for its --production/--menu defaults. That is the right behaviour for the shipped
/// CLI (see CliRunnerTests), but the wrong one for THIS test: Locate intentionally throws the
/// moment a second persona pack adds its own menu/source/*-menu-items.json (planned per
/// docs/persona-architecture.md), yet the real, untouched Python script never discovers anything
/// -- it hardcodes "personas/&lt;id&gt;/..." directly in its own POS_DATA_PATH/UI_MENU_PATH module
/// constants (relative to its own __file__) and would keep working fine. So this test now resolves
/// its fixtures the exact same (discovery-free) way the Python script resolves its own: by
/// inspecting POS_DATA_PATH/UI_MENU_PATH via `runpy.run_path` (the same technique
/// UpdateMenuSizes.Tests/PythonParityTests.cs already uses for PRODUCT_SEARCH_MAP), never through
/// ProductionExportLocator -- see <see cref="DotnetPort_ParityApproach_IsUnaffectedByASecondPersonaExport"/>
/// below for the regression test proving that.
///
/// Mutation check: reverting the Counter.most_common()-style stable tie-break (e.g. sorting
/// category groups alphabetically as a secondary key, or swapping the OrderByDescending for a
/// Dictionary-enumeration-order-based count) or reverting the ordinal name sort to a
/// case-insensitive/culture-aware one each independently makes the stdout-identical assertion
/// below fail against the real fixtures (confirmed while implementing this port -- see
/// ReportBuilderTests for the equivalent synthetic-fixture unit tests with their own mutation-check
/// notes). Separately, moving fixture resolution in the main test below back to
/// ProductionExportLocator.Locate makes the regression test below fail (confirmed while
/// implementing this fix for PR #243 review R1).
///
/// Never touches the real, checked-in personas/&lt;id&gt;/menu/** files directly as a write target --
/// this tool never writes to them in the first place (read-only report), and the main test below
/// never copies scripts/extract_production_items.py either: it runs the real file, in place,
/// exactly as a developer would. Only the regression test below (which deliberately builds a
/// synthetic second-persona layout) copies files, into a per-test temp directory, matching this
/// repo's existing C# test convention (see UpdateMenuSizes.Tests/PythonParityTests.cs).
/// </summary>
public sealed class PythonParityTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "squanchy-extract-production-items-parity-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DotnetPort_ProducesStdoutIdenticalOutput_ToRealPythonScript()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var realScript = Path.Combine(repoRoot, "scripts", "extract_production_items.py");
        Assert.True(File.Exists(realScript), $"Python twin not found: {realScript}");

        var interpreter = await RequirePythonInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        // Run the REAL script, unmodified, from its real location in the repo -- no throwaway
        // copy. extract_production_items.py is read-only (a report, no file mutation) and
        // resolves its own fixture paths purely from __file__, so this is exactly what
        // `python scripts/extract_production_items.py` does for a developer today.
        var (exitCode, pythonStdout, stderr) = await RunProcessAsync(
            interpreter, [realScript], TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Python twin exited {exitCode}.\nstdout:\n{pythonStdout}\nstderr:\n{stderr}");

        // Resolve the SAME two fixture paths the subprocess run above just read, straight from
        // the script's own module-level POS_DATA_PATH/UI_MENU_PATH constants (see
        // ResolvePythonScriptFixturePathsAsync) -- never via ProductionExportLocator -- so this
        // comparison is guaranteed to read the exact same files the Python run just did, no
        // matter how many persona packs happen to exist on disk.
        var (posDataPath, uiMenuPath) = await ResolvePythonScriptFixturePathsAsync(
            interpreter, realScript, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(posDataPath), $"Fixture not found: {posDataPath}");
        Assert.True(File.Exists(uiMenuPath), $"Fixture not found: {uiMenuPath}");

        var production = ProductionItemsExtractor.ExtractProductionItems(posDataPath);
        var uiItems = ProductionItemsExtractor.LoadUiItems(uiMenuPath);
        var report = ProductionItemsExtractor.BuildReport(production, uiItems);

        // Reconstruct what Program.cs would have printed (one Console.WriteLine per report line,
        // each terminated by Environment.NewLine, matching how Python's print() terminates every
        // line including the final one) and compare it to Python's own captured stdout verbatim --
        // this proves the category-count stable tie-break, the ordinal name sort, the ": .2f"-style
        // price formatting, and the gap-analysis set arithmetic all match exactly against real
        // production data, not just a synthetic unit-test sample.
        var dotnetStdout = string.Join(Environment.NewLine, report) + Environment.NewLine;
        Assert.Equal(pythonStdout, dotnetStdout);

        // Independent, human-readable cross-check on top of the raw stdout comparison above: both
        // programs' own "Total production items: N" line must agree, and must agree with what the
        // C# port's own data actually contains.
        var pythonTotal = ParseTotalProductionItems(pythonStdout);
        var dotnetTotal = ParseTotalProductionItems(dotnetStdout);
        Assert.Equal(pythonTotal, dotnetTotal);
        Assert.Equal(pythonTotal, production.Count);
    }

    /// <summary>
    /// PR #243 review R1 regression test. Builds a synthetic two-persona repo layout -- a copy of
    /// the real script plus the real persona's fixtures, alongside a second, unrelated persona
    /// pack whose only resemblance to a real one is having its own production export -- and proves
    /// two things: (a) <see cref="ProductionExportLocator.Locate"/> is genuinely ambiguous in that
    /// layout (confirming the regression this test targets is real, not hypothetical), and (b) the
    /// real Python script, run unmodified from that same layout, and this test file's own
    /// runpy-based fixture-resolution technique (<see cref="ResolvePythonScriptFixturePathsAsync"/>,
    /// used by <see cref="DotnetPort_ProducesStdoutIdenticalOutput_ToRealPythonScript"/> above) are
    /// both completely unaffected by it -- proving the parity test above would keep passing even
    /// once a second persona pack (one is planned per docs/persona-architecture.md) exists.
    /// </summary>
    [Fact]
    public async Task DotnetPort_ParityApproach_IsUnaffectedByASecondPersonaExport()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var realScript = Path.Combine(repoRoot, "scripts", "extract_production_items.py");
        var interpreter = await RequirePythonInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        var (posDataPath, uiMenuPath) = await ResolvePythonScriptFixturePathsAsync(
            interpreter, realScript, TestContext.Current.CancellationToken);

        // Derive the synthetic layout's paths from the REAL files' own repo-relative locations
        // (never a hardcoded persona-id literal -- the rebrand scanner forbids brand words in
        // code/tests, and this keeps the synthetic layout correct however the real persona pack
        // is ever renamed).
        var syntheticRepoRoot = Path.Combine(_tempRoot, "two-persona-repo");
        var syntheticPosDataPath = Path.Combine(syntheticRepoRoot, Path.GetRelativePath(repoRoot, posDataPath));
        var syntheticUiMenuPath = Path.Combine(syntheticRepoRoot, Path.GetRelativePath(repoRoot, uiMenuPath));
        Directory.CreateDirectory(Path.GetDirectoryName(syntheticPosDataPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(syntheticUiMenuPath)!);
        File.Copy(posDataPath, syntheticPosDataPath);
        File.Copy(uiMenuPath, syntheticUiMenuPath);

        var syntheticScriptsDir = Path.Combine(syntheticRepoRoot, "scripts");
        Directory.CreateDirectory(syntheticScriptsDir);
        var syntheticScript = Path.Combine(syntheticScriptsDir, "extract_production_items.py");
        File.Copy(realScript, syntheticScript);

        // A second, synthetic persona pack -- its own production export is all it takes to make
        // ProductionExportLocator.Locate ambiguous, exactly as a real second persona pack would.
        var secondPersonaSourceDir = Path.Combine(syntheticRepoRoot, "personas", "second-persona", "menu", "source");
        Directory.CreateDirectory(secondPersonaSourceDir);
        File.WriteAllText(Path.Combine(secondPersonaSourceDir, "second-persona-menu-items.json"), "{}");

        // (a) Confirm the regression this test targets is real: data-driven discovery (used by
        // this tool's own --production/--menu CLI default) is genuinely ambiguous in this layout.
        var locateException = Assert.Throws<InvalidOperationException>(() => ProductionExportLocator.Locate(syntheticRepoRoot));
        Assert.Contains("More than one persona has a production export", locateException.Message);

        // (b) But the real script, run unmodified from this same synthetic layout, still
        // succeeds -- it hardcodes "personas/<id>/..." directly and never globs, so an unrelated
        // second persona pack existing alongside it is irrelevant.
        var (exitCode, pythonStdout, stderr) = await RunProcessAsync(
            interpreter, [syntheticScript], TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Python twin exited {exitCode}.\nstdout:\n{pythonStdout}\nstderr:\n{stderr}");
        Assert.Contains("Total production items:", pythonStdout);

        // ... and resolving POS_DATA_PATH/UI_MENU_PATH via runpy against this same synthetic
        // layout -- the exact technique the main parity test above uses -- also succeeds, and
        // still points at the same two fixture files just copied in: proof that the main test's
        // own fixture-resolution approach is immune to the second-persona-export regression this
        // test targets.
        var (resolvedPosDataPath, resolvedUiMenuPath) = await ResolvePythonScriptFixturePathsAsync(
            interpreter, syntheticScript, TestContext.Current.CancellationToken);
        Assert.Equal(syntheticPosDataPath, resolvedPosDataPath);
        Assert.Equal(syntheticUiMenuPath, resolvedUiMenuPath);
    }

    /// <summary>
    /// Resolves a Python script's own module-level POS_DATA_PATH/UI_MENU_PATH constants by running
    /// it through <c>runpy.run_path</c> (the same technique
    /// UpdateMenuSizes.Tests/PythonParityTests.cs uses to read PRODUCT_SEARCH_MAP out of
    /// update_menu_sizes.py). <c>runpy.run_path</c>'s default <c>run_name</c> is
    /// <c>"&lt;run_path&gt;"</c>, not <c>"__main__"</c>, so the script's own
    /// <c>if __name__ == "__main__": main()</c> guard never fires -- only its module-level
    /// constants are evaluated, with zero file I/O against the real fixtures from this call alone.
    /// This is how both tests above locate their fixtures without ever calling
    /// <see cref="ProductionExportLocator.Locate"/>.
    /// </summary>
    private static async Task<(string PosDataPath, string UiMenuPath)> ResolvePythonScriptFixturePathsAsync(
        string interpreter, string scriptPath, CancellationToken cancellationToken)
    {
        const string inspectScript =
            "import json, runpy, sys\n" +
            "ns = runpy.run_path(sys.argv[1])\n" +
            "print(json.dumps({'pos': ns['POS_DATA_PATH'], 'ui': ns['UI_MENU_PATH']}))\n";

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            interpreter, ["-c", inspectScript, scriptPath], cancellationToken);
        Assert.True(exitCode == 0, $"Python path-inspection exited {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        using var document = JsonDocument.Parse(stdout.Trim());
        var posDataPath = document.RootElement.GetProperty("pos").GetString()!;
        var uiMenuPath = document.RootElement.GetProperty("ui").GetString()!;
        return (posDataPath, uiMenuPath);
    }

    private static int ParseTotalProductionItems(string stdout)
    {
        var match = Regex.Match(stdout, @"Total production items: (\d+)");
        Assert.True(match.Success, $"Could not find a 'Total production items: N' line in:\n{stdout}");
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>
    /// Resolves a working Python interpreter via <see cref="FindWorkingPythonInterpreterAsync"/>,
    /// or fails/skips the calling test: a missing interpreter is a legitimate local-dev skip, but a
    /// hard failure in CI (extract_production_items.py needs only the standard library, so CI must
    /// always have one).
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
            "(json, os, re, collections), so any Python 3 interpreter works.";
        if (isCi)
        {
            Assert.Fail(message + " CI must have a Python interpreter available -- this is not a legitimate skip in CI.");
        }
        Assert.Skip(message + " Install Python 3, or run `python -m venv .venv` at the repo root, to run this test locally.");
        return null;
    }

    /// <summary>
    /// Candidate interpreters in preference order: the repo-root .venv (same convention as
    /// tests/conformance/src/Conformance.Harness/RepoPaths.cs::PythonExecutable and
    /// UpdateMenuSizes.Tests/PythonParityTests.cs), then a bare "python3"/"python" on PATH for a
    /// developer machine without one. extract_production_items.py needs only the standard library,
    /// so the FIRST interpreter that runs at all qualifies.
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
            // Without this, .NET decodes the child's redirected stdout using the console's legacy
            // codepage (not UTF-8), mangling the non-ASCII/box-drawing/emoji characters this very
            // test is trying to prove survive round-tripping unescaped (e.g. "─", "⛔", "✅").
            // PYTHONIOENCODING forces Python's side of the pipe to also encode as UTF-8, regardless
            // of the host console's codepage -- the exact UnicodeEncodeError this port's own
            // Program.cs works around by forcing Console.OutputEncoding = Encoding.UTF8.
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

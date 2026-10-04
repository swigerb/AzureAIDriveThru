using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ExtractProductionItems.Tests;

/// <summary>
/// Issue #16's acceptance bar for a ported tool: "the C# tool produces the same output as its
/// Python twin." This drives the REAL scripts/extract_production_items.py as a genuine subprocess
/// against the real, checked-in persona fixtures (discovered via <see cref="ProductionExportLocator"/>,
/// not a hardcoded persona-id literal), drives <see cref="ProductionItemsExtractor"/>'s equivalent
/// pipeline in-process against the SAME files, then asserts the two programs' captured stdout is
/// identical byte-for-byte -- not just structurally equal. Unlike update_menu_sizes.py, this tool
/// is read-only (a report, no file mutation), so there is no output-file comparison here, only
/// stdout.
///
/// Mutation check: reverting the Counter.most_common()-style stable tie-break (e.g. sorting
/// category groups alphabetically as a secondary key, or swapping the OrderByDescending for a
/// Dictionary-enumeration-order-based count) or reverting the ordinal name sort to a
/// case-insensitive/culture-aware one each independently makes the stdout-identical assertion
/// below fail against the real fixtures (confirmed while implementing this port -- see
/// ReportBuilderTests for the equivalent synthetic-fixture unit tests with their own mutation-check
/// notes).
///
/// Never touches the real, checked-in personas/&lt;id&gt;/menu/** files directly as a write target --
/// this tool never writes to them in the first place (read-only report), and the real
/// scripts/extract_production_items.py is run from a throwaway copy under a per-test temp
/// directory, matching this repo's existing C# test convention (see
/// UpdateMenuSizes.Tests/PythonParityTests.cs).
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
        var discovery = ProductionExportLocator.Locate(repoRoot);
        var realScript = Path.Combine(repoRoot, "scripts", "extract_production_items.py");

        Assert.True(File.Exists(discovery.ProductionFilePath), $"Fixture not found: {discovery.ProductionFilePath}");
        Assert.True(File.Exists(discovery.MenuFilePath), $"Fixture not found: {discovery.MenuFilePath}");
        Assert.True(File.Exists(realScript), $"Python twin not found: {realScript}");

        var interpreter = await RequirePythonInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            return;
        }

        // Lay out a private "repo" under _tempRoot containing only what
        // extract_production_items.py's __file__-relative paths need:
        // scripts/extract_production_items.py, and a copy of the real production export + menu
        // file (read-only -- this tool never writes to them). The Python twin (kept untouched per
        // issue #16's scope) hardcodes "personas/<a specific persona id>/menu/..." literally in
        // its own source, not whatever persona ProductionExportLocator discovers -- so this layout
        // intentionally uses discovery.PersonaId (today, the one persona pack checked into the
        // repo) rather than a literal, but if that persona pack is ever renamed or a second
        // one is added, the Python run itself would need updating first (out of scope for this C#
        // port, same caveat as UpdateMenuSizes.Tests/PythonParityTests.cs).
        var pythonScriptsDir = Path.Combine(_tempRoot, "python-run", "scripts");
        var pythonMenuDir = Path.Combine(_tempRoot, "python-run", "personas", discovery.PersonaId, "menu");
        Directory.CreateDirectory(pythonScriptsDir);
        Directory.CreateDirectory(Path.Combine(pythonMenuDir, "source"));
        File.Copy(realScript, Path.Combine(pythonScriptsDir, "extract_production_items.py"));
        File.Copy(discovery.ProductionFilePath, Path.Combine(pythonMenuDir, "source", Path.GetFileName(discovery.ProductionFilePath)));
        File.Copy(discovery.MenuFilePath, Path.Combine(pythonMenuDir, "menuItems.json"));

        var (exitCode, pythonStdout, stderr) = await RunProcessAsync(
            interpreter, [Path.Combine(pythonScriptsDir, "extract_production_items.py")], TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Python twin exited {exitCode}.\nstdout:\n{pythonStdout}\nstderr:\n{stderr}");

        // Same two input fixtures, run through the C# port in-process instead -- read-only, so no
        // throwaway copy is strictly needed, but the real, checked-in fixtures are used directly
        // (ProductionItemsExtractor never mutates its inputs).
        var production = ProductionItemsExtractor.ExtractProductionItems(discovery.ProductionFilePath);
        var uiItems = ProductionItemsExtractor.LoadUiItems(discovery.MenuFilePath);
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

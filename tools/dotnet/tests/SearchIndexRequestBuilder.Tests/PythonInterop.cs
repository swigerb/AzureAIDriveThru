using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Resolves and runs a Python 3 interpreter for this test project's subprocess-based parity tests.
/// Same shape/rationale as ExtractProductionItems.Tests/PythonParityTests.cs's private helpers of
/// the same names (duplicated rather than shared -- tools/dotnet has no shared test-support project
/// yet), split into its own file here because PythonParityTests.cs in THIS project already has
/// plenty of its own test-specific logic.
/// </summary>
internal static class PythonInterop
{
    /// <summary>
    /// Resolves a working Python interpreter, or fails/skips the calling test: a missing
    /// interpreter is a legitimate local-dev skip, but a hard failure in CI -- this harness needs
    /// azure-search-documents/azure-identity/openai/jsonschema/pydantic/python-dotenv/rich/aiohttp
    /// (app/backend/requirements.txt, installed by .github/workflows/dotnet-tooling.yml for exactly
    /// this reason), so CI must always have a working interpreter with those installed.
    /// </summary>
    public static async Task<string?> RequireInterpreterAsync(string repoRoot, CancellationToken cancellationToken)
    {
        var interpreter = await FindWorkingInterpreterAsync(repoRoot, cancellationToken);
        if (interpreter is not null)
        {
            return interpreter;
        }

        var isCi = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        var message =
            "No Python interpreter was found (checked the repo-root .venv, then bare " +
            "\"python3\"/\"python\" on PATH) with azure-search-documents importable (and able to " +
            "import app/backend/setup_search_index.py's full dependency set: azure-identity, " +
            "openai, jsonschema, pydantic, python-dotenv, rich, aiohttp -- see " +
            "app/backend/requirements.txt).";
        if (isCi)
        {
            Assert.Fail(message + " CI must have these installed -- this is not a legitimate skip in CI.");
        }
        Assert.Skip(message + " Run `pip install -r app/backend/requirements.txt` to run this test locally.");
        return null;
    }

    /// <summary>Candidate interpreters in preference order: the repo-root .venv (same convention as
    /// ExtractProductionItems.Tests/PythonParityTests.cs and
    /// tests/conformance/src/Conformance.Harness/RepoPaths.cs), then a bare "python3"/"python" on
    /// PATH. Each candidate must ALSO be able to import azure.search.documents -- a bare interpreter
    /// without app/backend's dependencies installed would otherwise "pass" this check and then fail
    /// deep inside the harness script with a less actionable ModuleNotFoundError.</summary>
    private static async Task<string?> FindWorkingInterpreterAsync(string repoRoot, CancellationToken cancellationToken)
    {
        string[] candidates =
        [
            OperatingSystem.IsWindows()
                ? Path.Combine(repoRoot, ".venv", "Scripts", "python.exe")
                : Path.Combine(repoRoot, ".venv", "bin", "python"),
            "python3",
            "python",
        ];

        // Checks the FULL dependency set setup_search_index.py's import graph actually needs
        // (app/backend/requirements.txt), not just azure.search.documents: on a typical Windows
        // dev machine "python3" on PATH often resolves to a DIFFERENT installation than "python"
        // (observed locally: "python3" -> a separate install with azure-search-documents but no
        // jsonschema/pydantic, "python" -> the one with the full set) -- a shallower probe would
        // "succeed" on the wrong interpreter and then fail deep inside the harness subprocess with
        // a much less actionable ModuleNotFoundError.
        const string probe =
            "import azure.search.documents, azure.identity, openai, jsonschema, pydantic, jwt, aiohttp, dotenv, rich";

        foreach (var candidate in candidates)
        {
            try
            {
                var (exitCode, _, _) = await RunAsync(candidate, ["-c", probe], cancellationToken);
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

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Without this, .NET decodes the child's redirected stdout using the console's legacy
            // codepage (not UTF-8) on Windows. Persona menu names can contain non-ASCII characters
            // (e.g. "SuperSONIC(R)"), and PYTHONIOENCODING forces Python's side of the pipe to
            // encode as UTF-8 regardless of the host console's codepage.
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

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using UpdateMenuSizes;

namespace UpdateMenuSizes.Tests;

/// <summary>
/// Issue #16's acceptance bar for a ported tool: "the C# tool produces the same output as its
/// Python twin." This drives the REAL scripts/update_menu_sizes.py as a genuine subprocess
/// against a private copy of the real personas/sonic/menu/** fixtures, drives
/// <see cref="MenuSizeUpdater.UpdateMenu"/> in-process against a second private copy of the same
/// fixtures, then asserts the two resulting menuItems.json files are structurally identical JSON
/// (not byte-identical text -- json.dump's and System.Text.Json's indentation/escaping
/// conventions differ even when the parsed values are equal, and that difference isn't
/// meaningful here).
///
/// Never touches the real, checked-in personas/sonic/menu/** files -- both runs operate on throwaway
/// copies under a per-test temp directory, matching this repo's existing C# test convention (see
/// e.g. app/backend-dotnet/tests/Backend.Tests/TestSupport/PersonaPackFixture.cs,
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
    public async Task DotnetPort_ProducesStructurallyIdenticalOutput_ToRealPythonScript()
    {
        var repoRoot = RepoRoot.Find(AppContext.BaseDirectory);
        var realProductionFile = Path.Combine(
            repoRoot, "personas", "sonic", "menu", "source", "sonic-menu-items.json");
        var realMenuFile = Path.Combine(repoRoot, "personas", "sonic", "menu", "menuItems.json");
        var realScript = Path.Combine(repoRoot, "scripts", "update_menu_sizes.py");

        Assert.True(File.Exists(realProductionFile), $"Fixture not found: {realProductionFile}");
        Assert.True(File.Exists(realMenuFile), $"Fixture not found: {realMenuFile}");
        Assert.True(File.Exists(realScript), $"Python twin not found: {realScript}");

        var interpreter = await FindWorkingPythonInterpreterAsync(repoRoot, TestContext.Current.CancellationToken);
        if (interpreter is null)
        {
            var isCi = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
            var message =
                "No Python interpreter was found (checked the repo-root .venv, then bare " +
                "\"python3\"/\"python\" on PATH). This test needs only the standard library " +
                "(json, pathlib), so any Python 3 interpreter works.";
            if (isCi)
            {
                Assert.Fail(message + " CI must have a Python interpreter available -- this is not a legitimate skip in CI.");
            }
            Assert.Skip(message + " Install Python 3, or run `python -m venv .venv` at the repo root, to run this test locally.");
            return;
        }

        // Lay out a private "repo" under _tempRoot containing only what update_menu_sizes.py's
        // __file__-relative paths need: scripts/update_menu_sizes.py, and a copy of the real
        // production export + menu file for the Python run to mutate.
        var pythonScriptsDir = Path.Combine(_tempRoot, "python-run", "scripts");
        var pythonMenuDir = Path.Combine(_tempRoot, "python-run", "personas", "sonic", "menu");
        Directory.CreateDirectory(pythonScriptsDir);
        Directory.CreateDirectory(Path.Combine(pythonMenuDir, "source"));
        File.Copy(realScript, Path.Combine(pythonScriptsDir, "update_menu_sizes.py"));
        File.Copy(realProductionFile, Path.Combine(pythonMenuDir, "source", "sonic-menu-items.json"));
        var pythonMenuPath = Path.Combine(pythonMenuDir, "menuItems.json");
        File.Copy(realMenuFile, pythonMenuPath);

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            interpreter, [Path.Combine(pythonScriptsDir, "update_menu_sizes.py")], TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"Python twin exited {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        // Same two input fixtures, run through the C# port in-process instead.
        var dotnetDir = Path.Combine(_tempRoot, "dotnet-run");
        Directory.CreateDirectory(dotnetDir);
        var dotnetProductionPath = Path.Combine(dotnetDir, "sonic-menu-items.json");
        File.Copy(realProductionFile, dotnetProductionPath);
        var dotnetMenuPath = Path.Combine(dotnetDir, "menuItems.json");
        File.Copy(realMenuFile, dotnetMenuPath);

        MenuSizeUpdater.UpdateMenu(dotnetProductionPath, dotnetMenuPath);

        using var pythonDoc = JsonDocument.Parse(File.ReadAllText(pythonMenuPath));
        using var dotnetDoc = JsonDocument.Parse(File.ReadAllText(dotnetMenuPath));
        Assert.True(
            JsonStructurallyEquals(pythonDoc.RootElement, dotnetDoc.RootElement),
            "The C# port's menuItems.json output differs structurally from the Python twin's output.");
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
        };
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

    /// <summary>
    /// Deep JSON equality: object property order is NOT significant (json.dump and
    /// System.Text.Json may legitimately order/escape things differently), array element order
    /// IS significant (these documents are ordered lists of menu categories/items/sizes, where
    /// order is meaningful data, not incidental formatting).
    /// </summary>
    private static bool JsonStructurallyEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var aProps = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var bProps = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                if (aProps.Count != bProps.Count)
                {
                    return false;
                }
                foreach (var (name, aValue) in aProps)
                {
                    if (!bProps.TryGetValue(name, out var bValue) || !JsonStructurallyEquals(aValue, bValue))
                    {
                        return false;
                    }
                }
                return true;

            case JsonValueKind.Array:
                var aItems = a.EnumerateArray().ToList();
                var bItems = b.EnumerateArray().ToList();
                if (aItems.Count != bItems.Count)
                {
                    return false;
                }
                return aItems.Zip(bItems, JsonStructurallyEquals).All(equal => equal);

            case JsonValueKind.Number:
                return a.GetDecimal() == b.GetDecimal();

            case JsonValueKind.String:
                return a.GetString() == b.GetString();

            default: // True, False, Null, Undefined
                return true;
        }
    }
}

using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #143's own final acceptance item: "Harness unit tests proving the fake issuer's
/// discovery, JWKS and minted tokens validate with stock JwtBearer (C#) and PyJWT (Python)."
///
/// This is the Python half, run as a real subprocess against Scripts/verify_fake_entra_token.py
/// -- deliberately a subprocess rather than any kind of in-process Python interop, so this is a
/// genuine, independent PyJWT process validating a real HTTP-served discovery document and JWKS,
/// with zero shared in-memory state with the C# side. Resolves the interpreter the same way
/// <see cref="PythonBackendLauncher"/> does (<see cref="RepoPaths.PythonExecutable"/>, a
/// repo-root .venv), falling back to a bare "python"/"python3" on PATH for a developer machine
/// that hasn't run `python -m venv .venv` yet -- this test needs only PyJWT (see
/// tests/conformance/requirements-harness.txt), not the full app/backend runtime dependency set
/// PythonBackendLauncher's own .venv carries.
///
/// Skips gracefully only on a genuine local developer machine with no interpreter with PyJWT
/// importable found -- CI's python leg installs requirements-harness.txt into its own .venv (see
/// .github/workflows/conformance.yml), so this actually runs for real there, and R11 (Rick's PR
/// #158 round 1 review) makes CI fail loudly instead of skipping if that install step ever
/// regresses (via <see cref="PyJwtInteropPolicy"/>, the same "must never silently disappear in
/// CI" shape as <see cref="BrowserChannelPolicy"/>/<see cref="DotnetPlaceholderPolicy"/>). The
/// dotnet leg's CI job never sets up a Python interpreter at all (a completely separate,
/// pre-existing scoping decision, not one #143 makes) and is not CI for this suite's own purposes
/// either, so it still skips there, same as a local run against CONFORMANCE_BACKEND=dotnet would.
/// </summary>
public sealed class FakeEntraIssuerPyJwtValidationTests
{
    private static readonly string ScriptPath = Path.Combine(
        RepoPaths.FindRepoRoot(), "tests", "conformance", "tests", "Conformance.Tests", "Scripts",
        "verify_fake_entra_token.py");

    /// <summary>
    /// Candidate interpreters in preference order: the repo-root .venv (matching
    /// <see cref="PythonBackendLauncher"/>'s own convention, and what CI's python leg installs
    /// PyJWT into) first, then whatever bare "python"/"python3" a developer's PATH offers.
    /// </summary>
    private static IEnumerable<string> CandidateInterpreters()
    {
        yield return RepoPaths.PythonExecutable(RepoPaths.FindRepoRoot());
        yield return "python";
        yield return "python3";
    }

    /// <summary>The first candidate interpreter with PyJWT importable, or null if none qualify.</summary>
    private static async Task<string?> FindInterpreterWithPyJwtAsync(CancellationToken cancellationToken)
    {
        foreach (var candidate in CandidateInterpreters())
        {
            if (await CanImportPyJwtAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// R11 (Rick's PR #158 round 1 review): resolves an interpreter or ends the test via
    /// <see cref="PyJwtInteropPolicy"/> -- a clean local skip with the install command, or a hard
    /// CI failure so this proof can never silently disappear on a green leg. Shared by all three
    /// [Fact]s below so the CI-fail-closed decision lives in exactly one place.
    /// </summary>
    private static async Task<string> RequireInterpreterWithPyJwtAsync(CancellationToken cancellationToken)
    {
        var interpreter = await FindInterpreterWithPyJwtAsync(cancellationToken).ConfigureAwait(false);
        if (interpreter is not null)
        {
            return interpreter;
        }

        var isCi = CiEnvironment.IsCi;
        var message = PyJwtInteropPolicy.BuildMessage(interpreterFound: false, isCi);
        if (PyJwtInteropPolicy.ShouldSkip(interpreterFound: false, isCi))
        {
            Assert.Skip(message);
        }
        else
        {
            Assert.Fail(message);
        }

        throw new InvalidOperationException("Unreachable: Assert.Skip/Assert.Fail always end the test.");
    }

    private static async Task<bool> CanImportPyJwtAsync(string interpreter, CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, _, _) = await RunProcessAsync(
                interpreter, ["-c", "import jwt"], cancellationToken).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // Interpreter itself doesn't exist on this machine/PATH -- not a PyJWT problem, just
            // an absent candidate; move on to the next one.
            return false;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> VerifyAsync(
        string interpreter, string issuerUrl, string audience, string token, CancellationToken cancellationToken) =>
        await RunProcessAsync(interpreter, [ScriptPath, issuerUrl, audience, token], cancellationToken)
            .ConfigureAwait(false);

    [Fact]
    public async Task Valid_minted_token_validates_with_stock_PyJWT()
    {
        var ct = TestContext.Current.CancellationToken;
        var interpreter = await RequireInterpreterWithPyJwtAsync(ct);

        await using var issuer = new FakeEntraIssuer();
        await issuer.StartAsync(ct);
        try
        {
            var token = issuer.Mint();

            var (exitCode, stdout, stderr) = await VerifyAsync(
                interpreter, issuer.Issuer, FakeEntraIssuer.DefaultClientId, token, ct);

            Assert.True(exitCode == 0, $"Expected PyJWT to accept a valid minted token. stdout: {stdout} stderr: {stderr}");
            Assert.Contains(FakeEntraIssuer.DefaultOid, stdout);
        }
        finally
        {
            await issuer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Token_signed_by_the_unpublished_key_is_rejected_by_PyJWT()
    {
        var ct = TestContext.Current.CancellationToken;
        var interpreter = await RequireInterpreterWithPyJwtAsync(ct);

        await using var issuer = new FakeEntraIssuer();
        await issuer.StartAsync(ct);
        try
        {
            var token = issuer.Mint(new FakeEntraTokenOverrides { Key = FakeEntraSigningKey.Unpublished });

            var (exitCode, _, stderr) = await VerifyAsync(
                interpreter, issuer.Issuer, FakeEntraIssuer.DefaultClientId, token, ct);

            Assert.NotEqual(0, exitCode);
            Assert.NotEmpty(stderr);
        }
        finally
        {
            await issuer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Expired_token_is_rejected_by_PyJWT()
    {
        var ct = TestContext.Current.CancellationToken;
        var interpreter = await RequireInterpreterWithPyJwtAsync(ct);

        await using var issuer = new FakeEntraIssuer();
        await issuer.StartAsync(ct);
        try
        {
            var token = issuer.Mint(new FakeEntraTokenOverrides
            {
                Exp = DateTimeOffset.UtcNow.AddMinutes(-20),
                Nbf = DateTimeOffset.UtcNow.AddMinutes(-30),
            });

            var (exitCode, _, stderr) = await VerifyAsync(
                interpreter, issuer.Issuer, FakeEntraIssuer.DefaultClientId, token, ct);

            Assert.NotEqual(0, exitCode);
            Assert.NotEmpty(stderr);
        }
        finally
        {
            await issuer.DisposeAsync();
        }
    }
}

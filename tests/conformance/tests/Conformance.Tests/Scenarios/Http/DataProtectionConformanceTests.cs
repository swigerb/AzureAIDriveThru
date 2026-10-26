using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Booting the real <c>Program.cs</c> in Entra mode leaves the framework's Data Protection key ring
/// alone: no key is created or written and the stack logs nothing at startup (see
/// <c>Backend/Auth/EphemeralDataProtection.cs</c> for why that is the wanted outcome).
///
/// Black-box on purpose: <c>Backend.Tests</c> proves the helper inside a TestServer with its own
/// copy of the auth wiring, but nothing there boots <c>Program.cs</c>, so only a real launch notices
/// the <c>EphemeralDataProtection.Configure</c> call being dropped, or moved in front of
/// <c>AddAuthentication</c>, which silently re-adds the key-ring hosted service.
///
/// Starts its own process through <see cref="DotnetBackendLauncher"/> instead of using the shared
/// fixture's: that process's startup output belongs to whichever collection launched it, and
/// <see cref="BackendLauncherFactory"/> hands back an output-less external backend when
/// <c>CONFORMANCE_BACKEND_URL</c> is set, which would make a "no such line" assertion pass vacuously.
///
/// C#-only: the python backend has no ASP.NET Data Protection stack, so the row skips on the python leg.
/// </summary>
[Trait("Dotnet", "ready")]
public sealed class DataProtectionConformanceTests
{
    private const string CSharpOnly =
        "C#-only (Backend/Auth/EphemeralDataProtection.cs); the python backend has no ASP.NET Data Protection stack.";

    private const string DataProtectionCategory = "Microsoft.AspNetCore.DataProtection";

    private static readonly Uri s_neverDialed = new("http://127.0.0.1:1/");

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public async Task Startup_in_Entra_mode_creates_no_key_ring_and_logs_nothing_from_Data_Protection()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // The backend's appsettings.json caps Microsoft.AspNetCore at Warning. That hides what the
        // key ring logs when it creates a key, and Windows (DPAPI) never reaches the unencrypted-key
        // warning at all, so the row would pass without proving anything on a developer machine.
        // Lowering only this one category to Trace makes every line the stack writes visible on
        // every OS, and the assertion below then needs zero of them.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTH_MODE"] = "Entra",
            ["ENTRA_TENANT_ID"] = FakeEntraIssuer.DefaultTenantId,
            ["ENTRA_CLIENT_ID"] = FakeEntraIssuer.DefaultClientId,
            ["ENTRA_INSTANCE"] = s_neverDialed.ToString(),
            [$"Logging__LogLevel__{DataProtectionCategory}"] = "Trace",
        };

        await using IBackendUnderTest backend = await DotnetBackendLauncher.StartAsync(
            BackendContract.ForPort(s_neverDialed, s_neverDialed, port: 0),
            new DotnetBackendOptions { ExtraEnvironment = environment },
            ct);

        // Positive control. The line is logged once every hosted service has finished starting, and
        // the key-ring one is a hosted service, so nothing it would log can land after this point.
        bool started = await backend.WaitForDiagnosticsAsync(
            output => output.Contains("Application started", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10),
            cancellationToken: ct);
        Assert.True(started, $"The backend never logged \"Application started\".\n{backend.DumpDiagnostics()}");

        await backend.WaitForOutputQuiescenceAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), ct);

        string diagnostics = backend.DumpDiagnostics();
        string[] dataProtectionLines =
        [
            .. diagnostics
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(DataProtectionCategory, StringComparison.Ordinal))
                .Select(line => line.Trim()),
        ];

        Assert.True(
            dataProtectionLines.Length == 0,
            "Startup must not touch the Data Protection key ring, but the backend logged " +
            $"{dataProtectionLines.Length} line(s) from it:\n{string.Join('\n', dataProtectionLines)}\n" +
            "Check that EphemeralDataProtection.Configure is still the last service registration in Program.cs.");
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Refs #259 (Rick's #267 review, required item (c)): the Python twin of <see
/// cref="DotnetBackendLauncherPortRaceTests"/> -- before this file existed, only the .NET
/// launcher's retry-on-bind-collision path had any end-to-end coverage; the Python retry path
/// that falls back to port 0 had none at all. Same manufacturing technique: occupy a real port
/// with a raw <see cref="TcpListener"/> before ever calling <see
/// cref="PythonBackendLauncher.StartAsync"/>, pointing the launched backend's own first attempt at
/// that exact occupied port so its aiohttp bind is certain to fail -- no reliance on a rare,
/// unreproducible timing window. A real Python backend process is launched (the repo's `.venv`
/// must exist and the frontend's static assets must be built -- same prerequisites as every other
/// test in this project that exercises <see cref="PythonBackendLauncher"/>).
///
/// No <c>[Trait("Dotnet", "ready")]</c> -- same convention as the other harness-only test classes
/// that exercise a specific launcher directly regardless of which backend
/// <c>CONFORMANCE_BACKEND</c> selects for the rest of the suite (<see
/// cref="DotnetBackendLauncherPortRaceTests"/>, <see cref="DotnetBackendLauncherStartInfoTests"/>,
/// <see cref="DotnetBackendBuildGateTests"/>, <see cref="PortRaceDetectionTests"/>): this only
/// needs to run once (the python-leg CI job's broader `Category!=Browser` filter, with no
/// `Dotnet=ready` requirement, already covers it).
/// </summary>
[Trait("Category", "Harness")]
[Trait("Dotnet", "n/a-harness")] // Issue #21: backend-agnostic harness self-test, never exercises app/backend or app/backend-dotnet.
public sealed class PythonBackendLauncherPortRaceTests
{
    // Never-dialled: app/backend's own HealthEndpoint-equivalent checks only check config
    // presence/format, never live reachability, so nothing needs to actually be listening at
    // these URIs for the launched backend to report healthy. Same pattern as
    // DotnetBackendLauncherPortRaceTests/Scenarios/Auth/AuthModeLaunchTests.cs's own
    // NeverDialedRealtime/NeverDialedSearch.
    private static readonly Uri NeverDialedRealtime = new("http://127.0.0.1:1/");
    private static readonly Uri NeverDialedSearch = new("http://127.0.0.1:1/");

    // Refs #259: without CONFORMANCE_TEST_HOOKS=1, BackendEnvironment.Build's default
    // RUNNING_IN_PRODUCTION=true makes app.py's startup guard refuse to start with no AUTH_MODE
    // configured (exactly the row AuthModeLaunchTests.Production_and_unconfigured_fails_fast
    // pins) -- same CONFORMANCE_TEST_HOOKS=1 every real BackendProfile (see BackendProfiles
    // .Default) sets, which downgrades RUNNING_IN_PRODUCTION to "false" so the guard doesn't fire.
    private static readonly PythonBackendOptions Options = new()
    {
        ExtraEnvironment = new Dictionary<string, string> { ["CONFORMANCE_TEST_HOOKS"] = "1" },
    };

    /// <summary>
    /// Mutation check: revert the retry loop in <see cref="PythonBackendLauncher.StartAsync"/>
    /// (e.g. delete the <c>catch (PortBindRaceException) when (attempt &lt; MaxStartAttempts)</c>
    /// clause, or have it rethrow unconditionally) and this test fails deterministically -- the
    /// occupied port below guarantees the first attempt's aiohttp bind fails, with no reliance on
    /// a rare timing window.
    /// </summary>
    [Fact]
    public async Task StartAsync_recovers_when_the_assigned_port_is_already_bound_by_someone_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var occupiedPort = NetworkUtils.GetFreeTcpPort();

        using var occupyingListener = new TcpListener(IPAddress.Loopback, occupiedPort);
        occupyingListener.Start();
        try
        {
            var contract = BackendContract.ForPort(NeverDialedRealtime, NeverDialedSearch, occupiedPort);

            await using var backend = await PythonBackendLauncher.StartAsync(
                contract, Options, ct);

            // The real backend can never have bound the port this test is still holding open --
            // proves the retry loop actually reassigned a fresh port and relaunched, not merely
            // that *some* exception got swallowed.
            Assert.NotEqual(occupiedPort, backend.BaseUri.Port);

            using var http = new HttpClient();
            using var response = await http.GetAsync(new Uri(backend.BaseUri, "/health"), ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            occupyingListener.Stop();
        }
    }

    /// <summary>
    /// Refs #259 (Rick's #267 review, required item (b)/(c)): mirrors <see
    /// cref="DotnetBackendLauncherPortRaceTests.StartAsync_every_retry_requests_port_0_even_though_the_first_attempt_used_an_explicit_port"/>
    /// for the Python launcher -- asserts directly on the real <see cref="ProcessStartInfo"/>
    /// handed to every attempt (via <see
    /// cref="PythonBackendLauncher.TestOnlyProcessStartInfoObserver"/>) rather than only on the
    /// end-to-end outcome (which a reintroduced <see cref="NetworkUtils.GetFreeTcpPort"/> retry
    /// would also satisfy, since it would also land on "a different port").
    ///
    /// Mutation check: revert the retry clause (<c>attemptContract = attemptContract with {{ Port
    /// = 0 }}</c>) back to re-probing via <see cref="NetworkUtils.GetFreeTcpPort"/> and this test
    /// fails deterministically -- the occupied port guarantees a first-attempt failure, so a
    /// retry is certain to happen, and its environment's PORT would then be some nonzero probed
    /// value instead of "0".
    /// </summary>
    [Fact]
    public async Task StartAsync_every_retry_requests_port_0_even_though_the_first_attempt_used_an_explicit_port()
    {
        var ct = TestContext.Current.CancellationToken;
        var occupiedPort = NetworkUtils.GetFreeTcpPort();

        using var occupyingListener = new TcpListener(IPAddress.Loopback, occupiedPort);
        occupyingListener.Start();

        var observedPorts = new List<string?>();
        PythonBackendLauncher.TestOnlyProcessStartInfoObserver = startInfo =>
        {
            lock (observedPorts)
            {
                observedPorts.Add(startInfo.Environment.TryGetValue("PORT", out var port) ? port : null);
            }
        };
        try
        {
            var contract = BackendContract.ForPort(NeverDialedRealtime, NeverDialedSearch, occupiedPort);

            await using var backend = await PythonBackendLauncher.StartAsync(
                contract, Options, ct);

            Assert.True(
                observedPorts.Count >= 2,
                $"Expected at least one retry (first attempt + at least one retry), observed " +
                $"{observedPorts.Count} attempt(s): {string.Join(", ", observedPorts)}.");

            // First attempt: whatever the caller explicitly requested (the occupied port, here).
            Assert.Equal(occupiedPort.ToString(), observedPorts[0]);

            // Every retry (everything after the first attempt) must always request port 0.
            for (var i = 1; i < observedPorts.Count; i++)
            {
                Assert.Equal("0", observedPorts[i]);
            }
        }
        finally
        {
            PythonBackendLauncher.TestOnlyProcessStartInfoObserver = null;
            occupyingListener.Stop();
        }
    }
}

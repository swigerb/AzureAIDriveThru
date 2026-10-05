using System.Net;
using System.Net.Sockets;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #240: end-to-end proof that <see cref="DotnetBackendLauncher.StartAsync"/> actually
/// recovers from a real Kestrel bind failure, not just a theoretical one. CI's own failure (PR
/// #224 run 37174516348, attempt 1: port 46037, <c>AddressInUseException</c>, backend exit code
/// 134) is the real-world evidence the fix responds to, but that exact
/// <see cref="NetworkUtils.GetFreeTcpPort"/> TOCTOU race could not be reproduced locally across 15
/// loop iterations of the Happy-Hour suite on this dev box (Linux CI vs. Windows dev timing/
/// SO_REUSEADDR differences -- see the PR body). This test manufactures a GUARANTEED bind
/// collision instead: it occupies a real port with its own <see cref="TcpListener"/> before ever
/// calling <see cref="DotnetBackendLauncher.StartAsync"/>, pointing the launched backend's own
/// first attempt at that exact occupied port, so its Kestrel bind is certain to fail. A real
/// backend process is launched (Backend.csproj must build -- same SDK requirement as every other
/// test in this project); nothing about the retry loop itself is mocked, since the whole point is
/// proving the real <see cref="DotnetBackendLauncher.StartAsync"/> retry path recovers end to end,
/// not just that <see cref="PortRaceDetection.ShouldRetry"/> returns true in isolation (already
/// covered separately by <see cref="PortRaceDetectionTests"/>).
///
/// No <c>[Trait("Dotnet", "ready")]</c> -- same convention as the other harness-only test classes
/// that exercise the dotnet launcher directly regardless of which backend CONFORMANCE_BACKEND
/// selects for the rest of the suite (<see cref="DotnetBackendLauncherStartInfoTests"/>,
/// <see cref="DotnetBackendBuildGateTests"/>, <see cref="PortRaceDetectionTests"/>): this only
/// needs to run once (the python-leg CI job's broader `Category!=Browser` filter, with no
/// `Dotnet=ready` requirement, already covers it) rather than counting toward the dotnet-leg
/// scenario floor <see cref="DotnetTraitCoverageTests"/> enforces.
/// </summary>
[Trait("Category", "Harness")]
public sealed class DotnetBackendLauncherPortRaceTests
{
    // Never-dialled: HealthEndpoint/StartupChecks (app/backend-dotnet/src/Backend/Health/*) only
    // check config presence/format, never live reachability, so nothing needs to actually be
    // listening at these URIs for the launched backend to report healthy. Same pattern as
    // Scenarios/Auth/AuthModeLaunchTests.cs's own NeverDialedRealtime/NeverDialedSearch.
    private static readonly Uri NeverDialedRealtime = new("http://127.0.0.1:1/");
    private static readonly Uri NeverDialedSearch = new("http://127.0.0.1:1/");

    /// <summary>
    /// Mutation check: revert the retry loop in <see cref="DotnetBackendLauncher.StartAsync"/>
    /// (e.g. delete the <c>catch (PortBindRaceException) when (attempt &lt; MaxStartAttempts)</c>
    /// clause, or have it rethrow unconditionally) and this test fails deterministically -- the
    /// occupied port below guarantees the first attempt's Kestrel bind fails, with no reliance on
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

            // #226 round 3 / #249 CI failure (run 37256187043): a bare DotnetBackendOptions() sets
            // no AUTH_MODE and DotnetBackendEnvironment.Build pins ASPNETCORE_ENVIRONMENT/
            // DOTNET_ENVIRONMENT to "Production" unconditionally, so the launched backend hit
            // EntraSettings' fail-fast ("AUTH_MODE=Development (and an unset AUTH_MODE) are refused
            // in Production") and exited with code 1 before ever reaching the retry loop this test
            // exists to prove. Every other fixture that launches the dotnet backend goes through
            // ConformanceFixture, which always supplies either Entra-mode env (EntraModeEnvironment)
            // or a non-Production BackendProfile (e.g. DevelopmentPassThrough) -- this test bypasses
            // ConformanceFixture entirely (it needs to occupy the port before the backend ever
            // starts), so it must supply an equivalent profile itself. Reuses
            // BackendProfiles.DevelopmentPassThrough's exact env (Not Production, AUTH_MODE
            // unconfigured/pass-through) rather than standing up a FakeEntraIssuer, since this test
            // only cares about proving the port-bind retry loop recovers, not about exercising auth.
            var options = new DotnetBackendOptions
            {
                ExtraEnvironment = BackendProfiles.DevelopmentPassThrough.ExtraEnvironment,
            };

            await using var backend = await DotnetBackendLauncher.StartAsync(
                contract, options, ct);

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
}

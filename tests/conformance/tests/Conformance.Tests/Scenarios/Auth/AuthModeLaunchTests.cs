using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Issue #143/ADR-002, persona-architecture.md 18.11 row 15 ("Modes: launch-and-exit rows") and
/// 18.5's own mode table: each case launches a raw backend process directly via
/// <see cref="BackendLauncherFactory.StartAsync"/> (bypassing <see cref="ConformanceFixture"/> and
/// its shared per-collection process -- these deliberately misconfigured env combinations must
/// each get their own fresh process) with never-dialled loopback URIs for the realtime/search
/// dependencies, since a genuinely fail-fast backend must exit before ever attempting to reach
/// them. Every case expects the launcher to throw -- and specifically NOT a
/// <see cref="TimeoutException"/>, which would mean the process hung instead of exiting, a bug in
/// its own right and not a pass for "fails fast".
///
/// Gated directly via <see cref="AuthRowCapability.ShouldSkipCurrentBackend"/> at the very top of
/// each test (not through <see cref="ConformanceFixture.RunAuthRowAsync"/>, since these tests
/// don't use a <see cref="ConformanceFixture"/> at all) -- this also means a skip short-circuits
/// before ever launching a process, rather than waiting out a health-check timeout for a launch
/// that, today, is known to actually succeed.
/// </summary>
public sealed class AuthModeLaunchTests
{
    // Never-dialled: a genuinely fail-fast backend must exit before attempting to reach either of
    // these, so nothing needs to actually be listening at them.
    private static readonly Uri NeverDialedRealtime = new("http://127.0.0.1:1/");
    private static readonly Uri NeverDialedSearch = new("http://127.0.0.1:1/");

    private static async Task AssertFailsFastAsync(IReadOnlyDictionary<string, string> extraEnvironment, string caseLabel)
    {
        var skipReason = AuthRowCapability.ShouldSkipCurrentBackend();
        if (skipReason is not null)
        {
            Assert.Skip(skipReason);
            return;
        }

        var port = NetworkUtils.GetFreeTcpPort();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BackendLauncherFactory.StartAsync(
            NeverDialedRealtime, NeverDialedSearch, port, extraEnvironment: extraEnvironment,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(
            ex is TimeoutException,
            $"{caseLabel}: expected the process to exit non-zero before the port opened, but it hung until the " +
            $"health-check timeout instead ({ex.GetType().Name}: {ex.Message}). A hang is a bug, not \"fails fast\".");
    }

    [Fact]
    public Task Production_and_unconfigured_fails_fast() =>
        AssertFailsFastAsync(new Dictionary<string, string> { ["RUNNING_IN_PRODUCTION"] = "true" }, "Production, unconfigured");

    [Fact]
    public Task Production_with_explicit_development_mode_fails_fast() =>
        AssertFailsFastAsync(
            new Dictionary<string, string> { ["RUNNING_IN_PRODUCTION"] = "true", ["AUTH_MODE"] = "Development" },
            "Production, AUTH_MODE=Development");

    [Fact]
    public Task Development_mode_with_both_ids_set_not_production_fails_fast() =>
        AssertFailsFastAsync(
            new Dictionary<string, string>
            {
                ["RUNNING_IN_PRODUCTION"] = "false",
                ["AUTH_MODE"] = "Development",
                ["ENTRA_TENANT_ID"] = FakeEntraIssuer.DefaultTenantId,
                ["ENTRA_CLIENT_ID"] = FakeEntraIssuer.DefaultClientId,
            },
            "AUTH_MODE=Development, both ids set, not Production");

    [Fact]
    public Task Development_mode_with_only_one_id_set_not_production_fails_fast() =>
        AssertFailsFastAsync(
            new Dictionary<string, string>
            {
                ["RUNNING_IN_PRODUCTION"] = "false",
                ["AUTH_MODE"] = "Development",
                ["ENTRA_TENANT_ID"] = FakeEntraIssuer.DefaultTenantId,
            },
            "AUTH_MODE=Development, only one id set, not Production");

    [Fact]
    public Task Unknown_auth_mode_fails_fast() =>
        AssertFailsFastAsync(
            new Dictionary<string, string> { ["RUNNING_IN_PRODUCTION"] = "false", ["AUTH_MODE"] = "Bogus" },
            "unknown AUTH_MODE");

    [Fact]
    public Task Entra_instance_pointed_at_a_non_loopback_http_host_fails_fast() =>
        AssertFailsFastAsync(
            new Dictionary<string, string>
            {
                ["RUNNING_IN_PRODUCTION"] = "false",
                ["AUTH_MODE"] = "Entra",
                ["ENTRA_TENANT_ID"] = FakeEntraIssuer.DefaultTenantId,
                ["ENTRA_CLIENT_ID"] = FakeEntraIssuer.DefaultClientId,
                ["ENTRA_INSTANCE"] = "http://example.com/",
            },
            "ENTRA_INSTANCE=http:// to a non-loopback host");

    /// <summary>
    /// Assumption flagged for review: 18.5/18.11 never pin a specific "placeholder id" literal --
    /// that detection is #144/#147's own implementation detail. The nil GUID is a reasonable,
    /// widely-used placeholder sentinel (never a real Entra object id), but is not a contractually
    /// pinned value; if #144/#147 chooses a different placeholder-detection rule that doesn't
    /// happen to catch the nil GUID specifically, this case's env may need revisiting alongside
    /// turning <see cref="AuthRowCapability.PythonEnforcesAuth"/>/<see
    /// cref="AuthRowCapability.DotnetEnforcesAuth"/> on.
    /// </summary>
    [Fact]
    public Task Entra_mode_with_a_placeholder_client_id_fails_fast() =>
        AssertFailsFastAsync(
            new Dictionary<string, string>
            {
                ["RUNNING_IN_PRODUCTION"] = "false",
                ["AUTH_MODE"] = "Entra",
                ["ENTRA_TENANT_ID"] = FakeEntraIssuer.DefaultTenantId,
                ["ENTRA_CLIENT_ID"] = "00000000-0000-0000-0000-000000000000",
            },
            "Entra mode, placeholder client id");
}

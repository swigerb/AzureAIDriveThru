namespace Conformance.Harness;

/// <summary>
/// Issue #143/ADR-002: the capability switch gating persona-architecture.md 18.11's auth-row
/// conformance tests per backend, mirroring <see cref="DotnetPlaceholderPolicy"/>'s own pattern --
/// a pure, unit-testable decision function instead of scattering
/// <c>Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND")</c> checks through every row test.
///
/// Both switches started OFF: neither backend enforced ADR-002 auth at first (this issue's own
/// critical acceptance criterion was that the *whole* suite stayed green on both legs while that
/// was true -- the backends simply ignored the extra ENTRA_*/token env and query params the
/// harness always sends). <see cref="PythonEnforcesAuth"/> flipped to true in issue #144 once
/// app/backend enforced it end to end; <see cref="DotnetEnforcesAuth"/> flips to true in issue
/// #147 now that app/backend-dotnet does too -- both legs are on as of #147. Per
/// persona-architecture.md 18.11's closing note: "Rows are turned on per backend when that
/// backend's auth lands ... through the existing DotnetPlaceholderPolicy pattern. The gate then
/// requires both legs" -- i.e. flipping one switch only turns the rows on for that one backend's
/// CI leg; the OTHER backend's leg still skips until its own switch flips too.
/// </summary>
public static class AuthRowCapability
{
    /// <summary>Flip to true in issue #144 once app/backend enforces ADR-002 auth end to end.</summary>
    public const bool PythonEnforcesAuth = true;

    /// <summary>Flip to true in issue #147 once app/backend-dotnet enforces ADR-002 auth end to end.</summary>
    public const bool DotnetEnforcesAuth = true;

    public const string SkipReason =
        "Auth-row conformance (issue #143/ADR-002, persona-architecture.md 18.11) is gated per " +
        "backend by AuthRowCapability -- neither PythonEnforcesAuth (issue #144) nor " +
        "DotnetEnforcesAuth (issue #147) is on yet, so today's backend ignores the extra " +
        "ENTRA_*/token env and query params and this row can't assert real enforcement.";

    /// <summary>
    /// Same CONFORMANCE_BACKEND/CONFORMANCE_BACKEND_URL resolution as
    /// <see cref="BackendLauncherFactory.StartAsync"/> -- duplicated deliberately (not shared) so
    /// this stays a pure function of explicit inputs for unit testing, matching
    /// <see cref="DotnetPlaceholderPolicy"/>'s own style. An explicit backend URL resolves to
    /// "external": <see cref="Enforces"/> treats that as never enforcing, since the harness has no
    /// way to know what a pre-existing external backend actually is.
    /// </summary>
    public static string ResolveBackendName(string? conformanceBackendUrl, string? conformanceBackend) =>
        !string.IsNullOrWhiteSpace(conformanceBackendUrl)
            ? "external"
            : (conformanceBackend ?? "python").Trim().ToLowerInvariant();

    /// <summary>True when <paramref name="backendName"/> (already resolved by
    /// <see cref="ResolveBackendName"/>) enforces ADR-002 auth today. An unknown/unrecognised name
    /// -- including "external" -- is never treated as enforcing, so an auth row only ever runs
    /// when it's certain which backend it's asserting against.</summary>
    public static bool Enforces(string backendName) => backendName switch
    {
        "python" => PythonEnforcesAuth,
        "dotnet" => DotnetEnforcesAuth,
        _ => false,
    };

    /// <summary>Convenience for a row test's own [Fact]/[Theory] body: reads
    /// CONFORMANCE_BACKEND/CONFORMANCE_BACKEND_URL from the real process environment and returns
    /// <see cref="SkipReason"/>, or null when this row should actually run against today's
    /// resolved backend.</summary>
    public static string? ShouldSkipCurrentBackend() =>
        Enforces(ResolveBackendName(
            Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND_URL"),
            Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND")))
            ? null
            : SkipReason;
}

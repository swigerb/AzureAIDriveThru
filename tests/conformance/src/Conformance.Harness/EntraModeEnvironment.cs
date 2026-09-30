using Conformance.Fakes;

namespace Conformance.Harness;

/// <summary>
/// Issue #143/ADR-002: the exact four env vars persona-architecture.md 18.5/18.11 say the default
/// fixture injects to run a backend in Entra mode against <see cref="FakeEntraIssuer"/> --
/// AUTH_MODE, ENTRA_TENANT_ID, ENTRA_CLIENT_ID, ENTRA_INSTANCE. Deliberately does NOT set
/// ENTRA_API_SCOPE/ENTRA_APP_ROLE/APP_SESSION_SECRET: the issue only lists these four, and every
/// backend's own default for the other three (18.5: "access_as_user"/"DriveThru.User") already
/// matches what <see cref="FakeEntraIssuer.Mint"/> defaults to, and APP_SESSION_SECRET is already
/// set randomly per launch by both <see cref="BackendEnvironment"/> and
/// <see cref="DotnetBackendEnvironment"/>.
/// </summary>
public static class EntraModeEnvironment
{
    public static IReadOnlyDictionary<string, string> Build(FakeEntraIssuer issuer) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AUTH_MODE"] = "Entra",
            ["ENTRA_TENANT_ID"] = FakeEntraIssuer.DefaultTenantId,
            ["ENTRA_CLIENT_ID"] = FakeEntraIssuer.DefaultClientId,
            ["ENTRA_INSTANCE"] = issuer.BaseUri.ToString(),
        };
}

namespace Conformance.Harness;

/// <summary>
/// Pure decision logic for whether <c>FakeEntraIssuerPyJwtValidationTests</c> -- issue
/// #143/ADR-002's own final acceptance item, "harness unit tests proving the fake issuer's
/// discovery, JWKS and minted tokens validate with stock JwtBearer (C#) and PyJWT (Python)" --
/// should skip or fail when no interpreter with PyJWT importable can be found. The same
/// "must never silently disappear in CI" shape as <see cref="BrowserChannelPolicy"/> and
/// <see cref="DotnetPlaceholderPolicy"/> (R11, Rick's PR #158 round 1 review): CI's python leg
/// always installs requirements-harness.txt into its own .venv
/// (.github/workflows/conformance.yml), so seeing no PyJWT-capable interpreter there means that
/// install step itself regressed, or the venv path broke -- not a legitimate reason to quietly
/// turn the suite's only PyJWT interop proof into a skip on an otherwise-green leg. Only a
/// genuine local developer machine that hasn't set up PyJWT yet may skip.
/// </summary>
public static class PyJwtInteropPolicy
{
    public const string ReasonPrefix =
        "No Python interpreter with PyJWT importable was found (checked the repo-root .venv and " +
        "bare python/python3 on PATH).";

    /// <summary>
    /// True only when no PyJWT-capable interpreter was found AND this doesn't look like CI --
    /// mirrors <see cref="BrowserChannelPolicy.ShouldSkip"/>: CI must never silently skip this
    /// proof, so there is deliberately no way to force a skip there.
    /// </summary>
    public static bool ShouldSkip(bool interpreterFound, bool isCi) => !interpreterFound && !isCi;

    /// <summary>
    /// The message to use for an xUnit dynamic skip (<see cref="ShouldSkip"/> true) or to fail
    /// with otherwise (mirrors <see cref="BrowserChannelPolicy.BuildMessage"/>). Only meaningful
    /// when <paramref name="interpreterFound"/> is false -- callers only need this once a suitable
    /// interpreter search has already come back empty.
    /// </summary>
    public static string BuildMessage(bool interpreterFound, bool isCi) =>
        ShouldSkip(interpreterFound, isCi)
            ? ReasonPrefix + " Skipping because this doesn't look like CI -- install it with: " +
              "python -m pip install -r tests/conformance/requirements-harness.txt (into the " +
              "repo-root .venv if you have one, matching PythonBackendLauncher's own convention)."
            : ReasonPrefix + " This FAILS the test by default so CI can never silently lose issue " +
              "#143's own PyJWT interop proof -- CI's python leg always installs " +
              "requirements-harness.txt into its own .venv, so seeing this in CI means that " +
              "install step itself regressed, or the venv path broke.";
}

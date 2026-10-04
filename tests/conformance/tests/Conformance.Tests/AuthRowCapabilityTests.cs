using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="AuthRowCapability"/> (issue #143/ADR-002), mirroring
/// <see cref="DotnetPlaceholderPolicyTests"/>'s own style: a pure function of explicit inputs,
/// exercised directly rather than through real (global, parallel-unsafe) process environment
/// variables.
/// </summary>
[Trait("Category", "Harness")]
public sealed class AuthRowCapabilityTests
{
    [Theory]
    [InlineData(null, null, "python")]
    [InlineData(null, "python", "python")]
    [InlineData(null, "PYTHON", "python")] // case-insensitive, same as BackendLauncherFactory's own resolution
    [InlineData(null, " dotnet ", "dotnet")] // tolerate incidental whitespace
    [InlineData(null, "dotnet", "dotnet")]
    [InlineData("http://127.0.0.1:5000/", "python", "external")] // an explicit URL always wins, regardless of CONFORMANCE_BACKEND
    [InlineData("", "dotnet", "dotnet")] // an empty/whitespace-only URL doesn't count as "explicit"
    public void ResolveBackendName_matches_BackendLauncherFactorys_own_resolution(
        string? conformanceBackendUrl, string? conformanceBackend, string expected)
    {
        Assert.Equal(expected, AuthRowCapability.ResolveBackendName(conformanceBackendUrl, conformanceBackend));
    }

    [Fact]
    public void Enforces_python_reflects_PythonEnforcesAuth()
    {
        Assert.Equal(AuthRowCapability.PythonEnforcesAuth, AuthRowCapability.Enforces("python"));
    }

    [Fact]
    public void Enforces_dotnet_reflects_DotnetEnforcesAuth()
    {
        Assert.Equal(AuthRowCapability.DotnetEnforcesAuth, AuthRowCapability.Enforces("dotnet"));
    }

    [Theory]
    [InlineData("external")]
    [InlineData("bogus")]
    [InlineData("")]
    public void Enforces_is_false_for_any_unrecognised_or_external_name(string backendName)
    {
        Assert.False(AuthRowCapability.Enforces(backendName));
    }

    /// <summary>
    /// Issue #143's own critical acceptance criterion, pinned as a unit test: while
    /// <see cref="AuthRowCapability.DotnetEnforcesAuth"/> is still off, every dotnet-leg row must
    /// skip. This test itself will start failing the moment #147 flips that switch to true -- at
    /// which point it should be updated (or removed) to match.
    /// </summary>
    [Fact]
    public void Enforces_is_false_for_dotnet_while_its_switch_is_still_off()
    {
        Assert.False(AuthRowCapability.DotnetEnforcesAuth);
        Assert.False(AuthRowCapability.Enforces("dotnet"));
    }

    /// <summary>
    /// Issue #144's own critical acceptance criterion, pinned as a unit test: once
    /// <see cref="AuthRowCapability.PythonEnforcesAuth"/> flips to true, every python-leg row must
    /// run (not skip).
    /// </summary>
    [Fact]
    public void Enforces_is_true_for_python_now_that_its_switch_is_on()
    {
        Assert.True(AuthRowCapability.PythonEnforcesAuth);
        Assert.True(AuthRowCapability.Enforces("python"));
    }

    [Fact]
    public void ShouldSkipCurrentBackend_reads_the_real_process_environment()
    {
        // Exercises the real-environment convenience overload directly (rather than duplicating
        // Environment.GetEnvironmentVariable plumbing here): whatever CONFORMANCE_BACKEND/
        // CONFORMANCE_BACKEND_URL happen to be set to in this process, the result must always
        // agree with the pure ResolveBackendName/Enforces combination above.
        var expectedSkip = !AuthRowCapability.Enforces(AuthRowCapability.ResolveBackendName(
            Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND_URL"),
            Environment.GetEnvironmentVariable("CONFORMANCE_BACKEND")));

        var actual = AuthRowCapability.ShouldSkipCurrentBackend();

        Assert.Equal(expectedSkip, actual is not null);
        if (actual is not null)
        {
            Assert.Equal(AuthRowCapability.SkipReason, actual);
        }
    }
}

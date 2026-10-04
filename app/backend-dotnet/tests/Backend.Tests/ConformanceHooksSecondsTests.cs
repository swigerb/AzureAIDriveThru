using Backend.Tests.Ordering;

namespace Backend.Tests;

// `Backend.Tests` does not implicitly see the root `Backend` namespace that
// <see cref="ConformanceHooks"/> lives in.
using Backend;

/// <summary>Issue #13 Wave 4: port tests for conformance_hooks.py's <c>seconds(env_var, default)</c>
/// -- <see cref="ConformanceHooks.Seconds"/>'s own doc comment explains why this is independent of
/// <see cref="ConformanceHooks.Now"/>. Pinned to the shared <see cref="ClockHookTestCollection"/>
/// (same reasoning as <c>HappyHourPricingTests</c>): this class also mutates the process-wide
/// <c>CONFORMANCE_TEST_HOOKS</c> env var that <see cref="ConformanceHooks.HooksEnabled"/> reads,
/// and xUnit runs different test classes in parallel by default.</summary>
[Collection(ClockHookTestCollection.Name)]
public sealed class ConformanceHooksSecondsTests : IDisposable
{
    private const string EnabledEnv = "CONFORMANCE_TEST_HOOKS";
    private const string DurationEnv = "CONFORMANCE_RATE_LIMIT_RETRY_DELAY_SECONDS_TEST";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnabledEnv, null);
        Environment.SetEnvironmentVariable(DurationEnv, null);
    }

    [Fact]
    public void ReturnsDefault_WhenHooksDisabled_EvenIfEnvVarSet()
    {
        Environment.SetEnvironmentVariable(EnabledEnv, null);
        Environment.SetEnvironmentVariable(DurationEnv, "9.5");

        Assert.Equal(1.5, ConformanceHooks.Seconds(DurationEnv, 1.5));
    }

    [Fact]
    public void ReturnsDefault_WhenHooksEnabled_ButEnvVarUnset()
    {
        Environment.SetEnvironmentVariable(EnabledEnv, "1");
        Environment.SetEnvironmentVariable(DurationEnv, null);

        Assert.Equal(1.5, ConformanceHooks.Seconds(DurationEnv, 1.5));
    }

    [Fact]
    public void ReturnsDefault_WhenHooksEnabled_ButEnvVarEmpty()
    {
        Environment.SetEnvironmentVariable(EnabledEnv, "1");
        Environment.SetEnvironmentVariable(DurationEnv, "");

        Assert.Equal(1.5, ConformanceHooks.Seconds(DurationEnv, 1.5));
    }

    [Fact]
    public void ReturnsOverride_WhenHooksEnabled_AndEnvVarSet()
    {
        Environment.SetEnvironmentVariable(EnabledEnv, "1");
        Environment.SetEnvironmentVariable(DurationEnv, "0.05");

        Assert.Equal(0.05, ConformanceHooks.Seconds(DurationEnv, 1.5));
    }

    [Fact]
    public void Throws_WhenHooksEnabled_AndEnvVarUnparseable()
    {
        Environment.SetEnvironmentVariable(EnabledEnv, "1");
        Environment.SetEnvironmentVariable(DurationEnv, "soon");

        var ex = Assert.Throws<InvalidOperationException>(() => ConformanceHooks.Seconds(DurationEnv, 1.5));
        Assert.Contains(DurationEnv, ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Throws_WhenHooksEnabled_AndEnvVarNotStrictlyPositive(string raw)
    {
        Environment.SetEnvironmentVariable(EnabledEnv, "1");
        Environment.SetEnvironmentVariable(DurationEnv, raw);

        Assert.Throws<InvalidOperationException>(() => ConformanceHooks.Seconds(DurationEnv, 1.5));
    }
}

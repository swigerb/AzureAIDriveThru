using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="PyJwtInteropPolicy"/> (R11, Rick's PR #158 round 1 review):
/// <c>FakeEntraIssuerPyJwtValidationTests</c> must never silently disappear in CI just because no
/// PyJWT-capable interpreter was found -- pure and process-free, mirroring
/// <see cref="DotnetPlaceholderPolicyTests"/>'s style for the analogous
/// <see cref="DotnetPlaceholderPolicy"/> decision function.
/// </summary>
[Trait("Category", "Harness")]
public sealed class PyJwtInteropPolicyTests
{
    [Fact]
    public void ShouldSkip_is_true_when_no_interpreter_found_and_not_CI()
    {
        Assert.True(PyJwtInteropPolicy.ShouldSkip(interpreterFound: false, isCi: false));
    }

    [Fact]
    public void ShouldSkip_is_false_when_no_interpreter_found_but_this_is_CI()
    {
        Assert.False(PyJwtInteropPolicy.ShouldSkip(interpreterFound: false, isCi: true));
    }

    [Fact]
    public void ShouldSkip_is_false_when_an_interpreter_was_found_locally()
    {
        Assert.False(PyJwtInteropPolicy.ShouldSkip(interpreterFound: true, isCi: false));
    }

    [Fact]
    public void ShouldSkip_is_false_when_an_interpreter_was_found_in_CI()
    {
        Assert.False(PyJwtInteropPolicy.ShouldSkip(interpreterFound: true, isCi: true));
    }

    [Fact]
    public void BuildMessage_offers_the_install_command_when_skipping_locally()
    {
        var message = PyJwtInteropPolicy.BuildMessage(interpreterFound: false, isCi: false);

        Assert.Contains("Skipping because this doesn't look like CI", message);
        Assert.Contains("requirements-harness.txt", message);
        Assert.Contains(PyJwtInteropPolicy.ReasonPrefix, message);
    }

    [Fact]
    public void BuildMessage_demands_a_hard_failure_in_CI()
    {
        var message = PyJwtInteropPolicy.BuildMessage(interpreterFound: false, isCi: true);

        Assert.Contains("FAILS the test", message);
        Assert.Contains(PyJwtInteropPolicy.ReasonPrefix, message);
        Assert.DoesNotContain("Skipping", message);
    }
}

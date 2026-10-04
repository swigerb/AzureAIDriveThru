using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// R7 pin (Rick's PR #158 round 1 review): unit tests for <see cref="BackendExitCodeParser"/>,
/// independent of any real process launch -- exercises exactly the two launchers' own message
/// shapes plus the negative case (a message that doesn't carry an exit code at all, e.g. a build
/// failure), so a mutation that breaks the regex or the parse would fail here without needing a
/// slow end-to-end launch.
/// </summary>
[Trait("Category", "Harness")]
public sealed class BackendExitCodeParserTests
{
    [Theory]
    [InlineData("C# backend exited early (code 1) before becoming healthy.\n--- backend stdout/stderr ---\n", 1)]
    [InlineData("Python backend exited immediately (code 1), before ever attempting to bind the port.", 1)]
    [InlineData("Python backend exited early (code 137) before becoming healthy.\n", 137)]
    [InlineData("C# backend exited early (code -1) before becoming healthy.\n", -1)]
    public void Parses_the_exit_code_out_of_either_launcher_s_own_message_shape(string message, int expectedCode)
    {
        var parsed = BackendExitCodeParser.TryParse(message);

        Assert.Equal(expectedCode, parsed);
    }

    [Theory]
    [InlineData("Failed to start C# backend process 'dotnet \"Backend.dll\"'.")]
    [InlineData("Failed to start Python backend process 'python app.py'.")]
    [InlineData("C# backend did not report healthy at http://127.0.0.1:1/health within 30s.")]
    [InlineData("")]
    public void Returns_null_for_a_message_that_carries_no_exit_code_at_all(string message)
    {
        Assert.Null(BackendExitCodeParser.TryParse(message));
    }
}

using System.Text.RegularExpressions;

namespace Conformance.Harness;

/// <summary>
/// R7 (Rick's PR #158 round 1 review): a pure, unit-testable parser for the exit code both
/// <see cref="DotnetBackendLauncher"/> and <see cref="PythonBackendLauncher"/> already embed in
/// their own "exited early"/"exited immediately" <see cref="InvalidOperationException"/> messages
/// -- lets <c>Scenarios/Auth/AuthModeLaunchTests.cs</c> assert the specific contract 18.5's mode
/// rows actually need (a non-zero exit code, observed before the port ever opened), not merely
/// "some exception was thrown" -- <c>Assert.ThrowsAnyAsync&lt;Exception&gt;</c> also accepted a
/// hang that happened to time out, a <c>PortBindRaceException</c>, or a launcher/build error, none
/// of which prove the fail-fast contract these rows exist to pin.
/// </summary>
public static class BackendExitCodeParser
{
    private static readonly Regex Pattern =
        new(@"exited (?:early|immediately) \(code (-?\d+)\)", RegexOptions.Compiled);

    /// <summary>
    /// Extracts the exit code from an "exited early (code N)" (<see cref="DotnetBackendLauncher"/>)
    /// or "exited immediately (code N)" (<see cref="PythonBackendLauncher"/>) message -- both
    /// launchers' own existing wording, unchanged by this fix -- or null when the message doesn't
    /// carry that shape at all (a build failure, a process-start failure, a health-check timeout,
    /// etc.: none of those are the "process exited fast with a specific code" case this exists to
    /// recognise).
    /// </summary>
    public static int? TryParse(string message)
    {
        var match = Pattern.Match(message);
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }
}

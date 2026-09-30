using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #158 round 1 review, R8: pins that no scenario builds a `/realtime` client request
/// URI by hand anymore. Every existing call site now goes through
/// <see cref="Conformance.Harness.RealtimeUris"/> (the raw
/// <see cref="System.Net.WebSockets.ClientWebSocket"/>/<see cref="System.Net.Http.HttpClient"/>
/// sites) or <see cref="Conformance.Harness.RealtimeBrowserClient"/> (everything else), both of
/// which attach the default `access_token`/`token` pair before connecting -- so under 18.3's check
/// order, once #144 enforces, these sites keep exercising the behaviour actually under test
/// instead of a 401 that never gets past the auth checks.
///
/// <c>Scenarios/Auth</c> is deliberately excluded: those rows exist specifically to send a
/// bad/missing/wrong token on purpose, and already manage their own tokens via
/// <see cref="Conformance.Harness.RealtimeBrowserClient.ConnectAsync"/>'s own attach*/override
/// parameters.
///
/// Style mirrors <see cref="DotnetTraitCoverageTests"/>: a source-scan guard against silent
/// regression, not a runtime behaviour test -- no fixture, no backend, sub-millisecond.
/// </summary>
public sealed class RealtimeUriLiteralScanTests
{
    // Matches a `/realtime` path segment ending a request URI (or the start of one, in an
    // interpolated string) -- immediately followed by `?` (a query string), a closing `"` (the
    // bare path with nothing after it), or `{` (an interpolation hole appended right after it) --
    // but NOT the fake upstream server's own `/openai/v1/realtime` path (a completely different
    // endpoint, on a completely different fake process, that never needs the backend's auth).
    private static readonly Regex RealtimePathLiteral =
        new(@"(?<!openai/v1)/realtime(?=[?""{]|$)", RegexOptions.Compiled);

    [Fact]
    public void No_scenario_outside_the_helper_RealtimeBrowserClient_or_Scenarios_Auth_builds_a_realtime_uri_by_hand()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var testsRoot = Path.Combine(repoRoot, "tests", "conformance", "tests", "Conformance.Tests");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            if (file.Contains($"{Path.DirectorySeparatorChar}Scenarios{Path.DirectorySeparatorChar}Auth{Path.DirectorySeparatorChar}"))
            {
                continue;
            }
            if (Path.GetFileName(file) == nameof(RealtimeUriLiteralScanTests) + ".cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!RealtimePathLiteral.IsMatch(line))
                {
                    continue;
                }
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                if (line.Contains("Assert.Contains", StringComparison.Ordinal) ||
                    line.Contains("Assert.DoesNotContain", StringComparison.Ordinal))
                {
                    continue;
                }
                // Server-side inspection of an already-received request's path (a fake backend's
                // own HttpListener routing), not a client building an outgoing request URI.
                if (line.Contains("AbsolutePath", StringComparison.Ordinal))
                {
                    continue;
                }
                // The HttpClient/raw-socket call sites (Scenarios/Sessions/ModelSelectionConformanceTests.cs,
                // Scenarios/Transport/HeartbeatPongSurvivalTests.cs) splice the literal together
                // locally from a query RealtimeUris.BuildQueryAsync already built (with the
                // default credentials attached) earlier in the same method.
                var precedingLines = string.Join('\n', lines[..i]);
                if (precedingLines.Contains("RealtimeUris.", StringComparison.Ordinal))
                {
                    continue;
                }

                violations.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "Found a hand-built \"/realtime\" URI literal outside RealtimeUris/RealtimeBrowserClient/" +
            "Scenarios/Auth (Rick's PR #158 round 1 review, R8) -- once #144 enforces auth, this call " +
            "site would 401 before the behaviour under test ever runs. Use " +
            "Conformance.Harness.RealtimeUris.WithDefaultCredentialsAsync/BuildQueryAsync instead:\n" +
            string.Join("\n", violations));
    }
}

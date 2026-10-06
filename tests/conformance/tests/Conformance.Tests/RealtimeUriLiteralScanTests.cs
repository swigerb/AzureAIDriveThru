using System.IO;
using System.Linq;
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
///
/// The actual per-file scan logic lives in <see cref="RealtimeUriLiteralScanner"/> (PR #158 round
/// 2 review, N2), so it can be unit tested directly against synthetic samples -- see
/// <c>RealtimeUriLiteralScannerTests</c> -- instead of only ever running against the real tree.
/// </summary>
[Trait("Dotnet", "n/a-harness")] // Issue #21: source-scan guard against silent regression, no fixture/no backend (see this class's own doc comment).
public sealed class RealtimeUriLiteralScanTests
{
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
            if (Path.GetFileName(file) is
                nameof(RealtimeUriLiteralScanTests) + ".cs" or
                nameof(RealtimeUriLiteralScanner) + ".cs" or
                nameof(RealtimeUriLiteralScannerTests) + ".cs")
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(repoRoot, file);
            violations.AddRange(RealtimeUriLiteralScanner.Scan(relativePath, File.ReadAllLines(file)));
        }

        Assert.True(violations.Count == 0,
            "Found a hand-built \"/realtime\" URI literal outside RealtimeUris/RealtimeBrowserClient/" +
            "Scenarios/Auth (Rick's PR #158 round 1 review, R8) -- once #144 enforces auth, this call " +
            "site would 401 before the behaviour under test ever runs. Use " +
            "Conformance.Harness.RealtimeUris.WithDefaultCredentialsAsync/BuildQueryAsync instead:\n" +
            string.Join("\n", violations));
    }
}

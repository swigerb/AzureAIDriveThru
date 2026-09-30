using System.IO;
using System.Text.RegularExpressions;

namespace Conformance.Tests;

/// <summary>
/// Pure scan logic behind <see cref="RealtimeUriLiteralScanTests"/> (Rick's PR #158 round 1
/// review, R8), extracted so it can be unit tested directly against synthetic <c>(path, lines)</c>
/// samples instead of only ever running against the real tree.
///
/// PR #158 round 2 review, N2: the original exemption was "does <c>RealtimeUris.</c> appear
/// anywhere earlier in the same file", which Rick proved is a hole -- a file that legitimately
/// uses the helper once (for an entirely different call) can still hand-build an uncredentialed
/// <c>/realtime</c> literal further down and the scan would let it through
/// (<c>Scenarios/Security/OriginValidationTests.cs:78</c> was the proof site). The fix narrows the
/// exemption to an explicit allow-list of the two known-legitimate splice sites, by
/// <c>(file name, exact trimmed line)</c>, rather than "this file mentions the helper somewhere".
/// </summary>
public static class RealtimeUriLiteralScanner
{
    // Matches a `/realtime` path segment ending a request URI (or the start of one, in an
    // interpolated string) -- immediately followed by `?` (a query string), a closing `"` (the
    // bare path with nothing after it), or `{` (an interpolation hole appended right after it) --
    // but NOT the fake upstream server's own `/openai/v1/realtime` path (a completely different
    // endpoint, on a completely different fake process, that never needs the backend's auth).
    private static readonly Regex RealtimePathLiteral =
        new(@"(?<!openai/v1)/realtime(?=[?""{]|$)", RegexOptions.Compiled);

    /// <summary>
    /// The only two known-legitimate hand-spliced <c>/realtime</c> request lines in the whole
    /// Conformance.Tests tree, keyed by file name. Both splice the literal together locally from a
    /// query <c>RealtimeUris.BuildQueryAsync</c> already built (with the default credentials
    /// attached) earlier in the same method -- see
    /// <c>Scenarios/Sessions/ModelSelectionConformanceTests.cs</c> and
    /// <c>Scenarios/Transport/HeartbeatPongSurvivalTests.cs</c>. Anything else that matches
    /// <see cref="RealtimePathLiteral"/> is a violation, full stop -- adding a new legitimate site
    /// requires adding it here explicitly, by exact trimmed line text, so a typo'd or
    /// credential-less near-miss still fails loudly instead of silently matching a loose pattern.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedSpliceSites =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ModelSelectionConformanceTests.cs"] =
                "using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(backendBaseUri, $\"/realtime?{fullQuery}\"));",
            ["HeartbeatPongSurvivalTests.cs"] =
                "$\"GET /realtime?{query} HTTP/1.1\\r\\n\" +",
        };

    /// <summary>
    /// Scans one file's lines for a hand-built <c>/realtime</c> request URI literal. Callers are
    /// responsible for deciding which files/directories to scan at all (bin/obj, the scanner's own
    /// test files, and <c>Scenarios/Auth</c>'s deliberate bad-token rows are excluded at that
    /// level, not here -- see <see cref="RealtimeUriLiteralScanTests"/>).
    /// </summary>
    /// <param name="relativePath">
    /// Path used only for the violation message and to resolve the file name against
    /// <see cref="AllowedSpliceSites"/>; does not need to exist on disk.
    /// </param>
    /// <param name="lines">The file's lines, in order.</param>
    public static IReadOnlyList<string> Scan(string relativePath, IReadOnlyList<string> lines)
    {
        var violations = new List<string>();
        var fileName = Path.GetFileName(relativePath);
        var allowedLine = AllowedSpliceSites.GetValueOrDefault(fileName);

        for (var i = 0; i < lines.Count; i++)
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
            // Server-side inspection of an already-received request's path (a fake backend's own
            // HttpListener routing), not a client building an outgoing request URI.
            if (line.Contains("AbsolutePath", StringComparison.Ordinal))
            {
                continue;
            }
            if (allowedLine is not null && line.Trim() == allowedLine)
            {
                continue;
            }

            violations.Add($"{relativePath}:{i + 1}: {line.Trim()}");
        }

        return violations;
    }
}

using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Unit tests for <see cref="RealtimeUriLiteralScanner"/> (PR #158 round 2 review, N2) against
/// synthetic <c>(path, lines)</c> samples -- including Rick's proof that the old exemption
/// ("does <c>RealtimeUris.</c> appear anywhere earlier in the same file") has a hole.
/// </summary>
[Trait("Category", "Harness")]
[Trait("Dotnet", "n/a-harness")] // Issue #21: backend-agnostic harness self-test, never exercises app/backend or app/backend-dotnet.
public sealed class RealtimeUriLiteralScannerTests
{
    [Fact]
    public void A_file_that_uses_the_helper_in_one_method_and_hand_builds_a_realtime_uri_in_another_is_one_violation()
    {
        // Rick's PR #158 round 2 review hole, reproduced as a synthetic sample: this is exactly
        // the shape of the mutation he proved at Scenarios/Security/OriginValidationTests.cs:78 --
        // one method legitimately calls RealtimeUris, a second, unrelated method in the SAME FILE
        // hand-builds an uncredentialed "/realtime" literal from unrelated variables. The old
        // "RealtimeUris. appears anywhere earlier in the file" exemption let this through; it must
        // now be a violation.
        var lines = new[]
        {
            "public sealed class SyntheticTests",
            "{",
            "    public async Task Legit_call_site()",
            "    {",
            "        var wsUri = await RealtimeUris.WithDefaultCredentialsAsync(backend, cancellationToken: ct);",
            "        await socket.ConnectAsync(wsUri, ct);",
            "    }",
            "",
            "    public async Task Hand_built_call_site()",
            "    {",
            "        var badUri = new Uri($\"ws://{backend.Host}:{backend.Port}/realtime\");",
            "        await socket.ConnectAsync(badUri, ct);",
            "    }",
            "}",
        };

        var violations = RealtimeUriLiteralScanner.Scan("Scenarios/Security/SyntheticTests.cs", lines);

        var violation = Assert.Single(violations);
        Assert.Contains(":11:", violation);
    }

    [Fact]
    public void The_ModelSelectionConformanceTests_splice_site_is_not_a_violation()
    {
        var lines = new[]
        {
            "    public static async Task<string> RealtimeConnectBodyAsync(",
            "        Uri backendBaseUri, string query, HttpStatusCode expectedStatus, CancellationToken ct)",
            "    {",
            "        using var http = ConformanceHttpClient.Create();",
            "        var fullQuery = await RealtimeUris.BuildQueryAsync(backendBaseUri, query, ct);",
            "        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(backendBaseUri, $\"/realtime?{fullQuery}\"));",
            "        request.Headers.TryAddWithoutValidation(\"Connection\", \"Upgrade\");",
            "    }",
        };

        var violations = RealtimeUriLiteralScanner.Scan(
            "tests/conformance/tests/Conformance.Tests/Scenarios/Sessions/ModelSelectionConformanceTests.cs", lines);

        Assert.Empty(violations);
    }

    [Fact]
    public void The_HeartbeatPongSurvivalTests_splice_site_is_not_a_violation()
    {
        var lines = new[]
        {
            "        var query = await RealtimeUris.BuildQueryAsync(backendUri, cancellationToken: ct);",
            "        var request =",
            "            $\"GET /realtime?{query} HTTP/1.1\\r\\n\" +",
            "            $\"Host: {backendUri.Host}:{backendUri.Port}\\r\\n\" +",
            "            \"Upgrade: websocket\\r\\n\";",
        };

        var violations = RealtimeUriLiteralScanner.Scan(
            "tests/conformance/tests/Conformance.Tests/Scenarios/Transport/HeartbeatPongSurvivalTests.cs", lines);

        Assert.Empty(violations);
    }

    [Fact]
    public void The_OriginValidationTests_portless_host_splice_site_is_not_a_violation()
    {
        // PR #230 round 2 review, item 3: Origin_with_explicit_default_port_is_rejected_against_a_
        // portless_host needs a raw TcpClient handshake (ClientWebSocket can't forge a custom,
        // deliberately portless Host header), mirroring HeartbeatPongSurvivalTests.cs's own
        // already-approved splice exactly -- including building the query via
        // RealtimeUris.BuildQueryAsync first, so the request still carries the default credentials.
        var lines = new[]
        {
            "        var query = await RealtimeUris.BuildQueryAsync(backend, cancellationToken: ct);",
            "        var request =",
            "            $\"GET /realtime?{query} HTTP/1.1\\r\\n\" +",
            "            $\"Host: {backend.Host}\\r\\n\" +",
            "            \"Upgrade: websocket\\r\\n\";",
        };

        var violations = RealtimeUriLiteralScanner.Scan(
            "tests/conformance/tests/Conformance.Tests/Scenarios/Security/OriginValidationTests.cs", lines);

        Assert.Empty(violations);
    }

    [Fact]
    public void An_allowed_splice_line_reformatted_in_the_wrong_file_is_still_a_violation()
    {
        // The allow-list is keyed by (file name, exact trimmed line) -- the same literal line
        // showing up in some other file is not automatically trusted just because it matches one
        // of the known-good shapes textually.
        var lines = new[]
        {
            "using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(backendBaseUri, $\"/realtime?{fullQuery}\"));",
        };

        var violations = RealtimeUriLiteralScanner.Scan(
            "Scenarios/Security/SomeOtherFile.cs", lines);

        Assert.Single(violations);
    }

    [Fact]
    public void A_hand_built_realtime_literal_elsewhere_in_OriginValidationTests_cs_is_still_a_violation()
    {
        // Guards against reopening the exact hole Rick proved at
        // Scenarios/Security/OriginValidationTests.cs:78 (PR #158 round 2 review, N2): being on the
        // allow-list for ONE specific line must not exempt a second, unrelated hand-built literal
        // elsewhere in the same file.
        var lines = new[]
        {
            "        var query = await RealtimeUris.BuildQueryAsync(backend, cancellationToken: ct);",
            "        var request =",
            "            $\"GET /realtime?{query} HTTP/1.1\\r\\n\" +",
            "            \"Host: whatever\\r\\n\\r\\n\";",
            "",
            "        var badUri = new Uri($\"ws://{backend.Host}:{backend.Port}/realtime\");",
        };

        var violations = RealtimeUriLiteralScanner.Scan(
            "tests/conformance/tests/Conformance.Tests/Scenarios/Security/OriginValidationTests.cs", lines);

        var violation = Assert.Single(violations);
        Assert.Contains(":6:", violation);
    }

    [Fact]
    public void A_comment_line_is_not_a_violation()
    {
        var violations = RealtimeUriLiteralScanner.Scan(
            "Scenarios/Security/SyntheticTests.cs",
            new[] { "        // await RealtimeUris.WithDefaultCredentialsAsync gives us /realtime\"" });

        Assert.Empty(violations);
    }

    [Fact]
    public void An_assert_contains_line_is_not_a_violation()
    {
        var violations = RealtimeUriLiteralScanner.Scan(
            "Scenarios/Security/SyntheticTests.cs",
            new[] { "        Assert.Contains(\"/realtime\", requestPath);" });

        Assert.Empty(violations);
    }

    [Fact]
    public void A_server_side_absolute_path_inspection_is_not_a_violation()
    {
        var violations = RealtimeUriLiteralScanner.Scan(
            "Scenarios/Security/SyntheticTests.cs",
            new[] { "        if (context.Request.Url!.AbsolutePath == \"/realtime\")" });

        Assert.Empty(violations);
    }

    [Fact]
    public void The_fake_upstream_openai_v1_realtime_path_is_not_a_violation()
    {
        var violations = RealtimeUriLiteralScanner.Scan(
            "Scenarios/Security/SyntheticTests.cs",
            new[] { "        listener.Prefixes.Add($\"http://localhost:{port}/openai/v1/realtime\");" });

        Assert.Empty(violations);
    }
}

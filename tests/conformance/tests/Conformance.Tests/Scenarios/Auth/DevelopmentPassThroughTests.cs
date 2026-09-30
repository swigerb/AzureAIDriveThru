using System.Net;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Issue #143/ADR-002, persona-architecture.md 18.11 row 16: "Development pass-through
/// (non-Production, unconfigured, with AUTH_MODE unset and with AUTH_MODE=Development) => 200
/// with no token; /realtime opens." Runs the same two assertions against both
/// <see cref="DevelopmentPassThroughFixture"/> (AUTH_MODE unset) and
/// <see cref="DevelopmentPassThroughExplicitModeFixture"/> (AUTH_MODE=Development), matching
/// 18.5's own "the same as the row above" equivalence between the two.
///
/// Not gated behind <see cref="AuthRowCapability"/>: unlike rows 1 to 15, this row's expected
/// behaviour ("no auth enforcement, plain 200/open") is exactly what today's backends already do
/// unconditionally (neither implements the mode resolver yet) -- so it's already a meaningful,
/// always-on assertion rather than something that can only pass once #144/#147 land.
/// </summary>
public abstract class DevelopmentPassThroughTestsBase(ConformanceFixture fixture)
{
    [Fact]
    public Task No_token_REST_is_still_200() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/personas"), ct).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    });

    [Fact]
    public Task Realtime_opens_with_no_token() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var browser = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, attachAccessToken: false, attachSessionToken: false, cancellationToken: ct)
            .ConfigureAwait(false);

        var created = await browser.ReceivedFrames
            .WaitForAsync(f => f.Type == "session.created", AuthRowRealtimeAssertions.FrameTimeout, ct)
            .ConfigureAwait(false);
        Assert.True(created is not null, "Row 16: expected /realtime to open with no token at all.");
    });
}

[Collection(DevelopmentPassThroughCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class DevelopmentPassThroughUnsetModeTests(DevelopmentPassThroughFixture fixture)
    : DevelopmentPassThroughTestsBase(fixture);

[Collection(DevelopmentPassThroughExplicitModeCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class DevelopmentPassThroughExplicitModeTests(DevelopmentPassThroughExplicitModeFixture fixture)
    : DevelopmentPassThroughTestsBase(fixture);

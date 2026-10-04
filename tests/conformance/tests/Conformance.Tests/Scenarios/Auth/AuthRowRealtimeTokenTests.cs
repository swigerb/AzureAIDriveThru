using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Issue #143/ADR-002, persona-architecture.md 18.11 rows 1 to 8/17 to 19 (including 6b) on
/// <c>/realtime</c> -- the realtime half of <c>AuthRowRestTests.cs</c>'s REST coverage, sharing
/// the exact same <see cref="AuthRowTokenCase.All"/> case table so a row's token shape and its
/// expected outcome can never drift between the two routes.
/// </summary>
[Collection(ConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
[AuthRowCapabilityGated]
public sealed class AuthRowRealtimeTokenTests(ConformanceFixture fixture)
{
    public static TheoryData<int> CaseIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < AuthRowTokenCase.All.Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public Task Row_asserts_on_realtime(int caseIndex) => fixture.RunAuthRowAsync(async () =>
    {
        var row = AuthRowTokenCase.All[caseIndex];
        var token = row.MintToken(fixture.EntraIssuer!);
        var ct = TestContext.Current.CancellationToken;

        await AuthRowRealtimeAssertions.AssertOutcomeAsync(
            fixture.Backend!.BaseUri,
            row.Expected,
            row.ToString(),
            ct,
            attachAccessToken: token is not null,
            accessToken: token,
            attachSessionToken: row.RealtimeAttachSessionToken).ConfigureAwait(false);
    });
}

using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Conformance.Fakes;
using Xunit;

namespace Conformance.Tests.Scenarios.Auth;

/// <summary>
/// Unit tests for <see cref="AuthRowTokenCase.All"/> and <see cref="FakeEntraIssuer.Mint"/> that
/// need only a started <see cref="FakeEntraIssuer"/> -- no backend -- so they pin the minting
/// layer itself, independent of what either backend does with the result.
/// </summary>
[Trait("Dotnet", "ready")]
public sealed class AuthRowCasesTests : IAsyncLifetime
{
    private readonly FakeEntraIssuer _issuer = new();

    public async ValueTask InitializeAsync() => await _issuer.StartAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync() => await _issuer.DisposeAsync().ConfigureAwait(false);

    /// <summary>R1 pin (Rick's PR #158 round 1 review): row 6 (and every other expiry row R2
    /// adds) must mint without throwing. Before the fix, row 6's <c>Exp = now - 10 min</c> with
    /// the default <c>Nbf = now - 5 min</c> left <c>Nbf</c> after <c>Exp</c>, and
    /// <see cref="JwtSecurityToken"/>'s constructor throws IDX12401 for that ordering -- a
    /// mutation that puts row 6's <c>Nbf</c> override back to the default would fail this test by
    /// throwing instead of returning.</summary>
    [Fact]
    public void Every_case_mints_without_throwing()
    {
        foreach (var row in AuthRowTokenCase.All)
        {
            var exception = Record.Exception(() => row.MintToken(_issuer));
            Assert.True(exception is null, $"{row}: MintToken threw {exception}.");
        }
    }

    /// <summary>R4 pin (Rick's PR #158 round 1 review): a default mint's <c>roles</c> claim must
    /// decode as a JSON array, even with a single element -- not the bare JSON string
    /// <see cref="JwtSecurityTokenHandler"/>'s own claim aggregation would otherwise collapse a
    /// single same-Type claim to (exactly what Rick's own probe found: <c>"roles":"DriveThru.User"</c>).
    /// Inspects the raw decoded payload JSON directly, the same way that probe did, rather than
    /// relying on <see cref="Claim"/>'s own array bookkeeping. A mutation that drops the
    /// post-construction <c>token.Payload["roles"] = roles.ToArray()</c> fix would fail this test:
    /// the decoded <c>roles</c> value would be a JSON string, not a JSON array.</summary>
    [Fact]
    public void Default_mint_roles_claim_is_a_json_array_of_length_one()
    {
        var token = _issuer.Mint();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var payload = System.Text.Json.JsonDocument.Parse(jwt.Payload.SerializeToJson());

        var roles = payload.RootElement.GetProperty("roles");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, roles.ValueKind);
        Assert.Equal(1, roles.GetArrayLength());
        Assert.Equal(FakeEntraIssuer.DefaultRole, roles[0].GetString());
    }

    /// <summary>R5 pin (Rick's PR #158 round 1 review): the three "published kid" row 7 variants
    /// must actually carry the published kid in their header -- a mutation that drops the
    /// <c>none</c>/HS256 <c>token.Header["kid"] = overrides.Kid</c> fix would fail this test,
    /// since those two shapes would decode with no <c>kid</c> header claim at all.</summary>
    [Theory]
    [InlineData("7 (bad signature, published kid)")]
    [InlineData("7 (alg: none, published kid)")]
    [InlineData("7 (HS256, published kid)")]
    public void Published_kid_row_7_variant_carries_the_published_kid_header(string rowName)
    {
        var row = AuthRowTokenCase.All.Single(r => r.Row == rowName);
        var token = row.MintToken(_issuer);
        Assert.NotNull(token);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token!);
        Assert.Equal(FakeEntraIssuer.PublishedKid, jwt.Header.Kid);
    }
}

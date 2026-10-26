using System.Net;
using System.Text.Json;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

[Collection(ConformanceCollection.Name)]
[Trait("Dotnet", "ready")]
public sealed class AuthSessionTests(ConformanceFixture fixture)
{
    [Fact]
    public Task Auth_session_endpoint_returns_a_token() => fixture.RunAsync(async () =>
    {
        using HttpClient http = ConformanceHttpClient.Create();
        using HttpResponseMessage response = await http.GetAsync(new Uri(fixture.Backend!.BaseUri, "/api/auth/session"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        string? token = document.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
    });
}

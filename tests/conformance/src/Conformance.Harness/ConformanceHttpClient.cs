using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;

namespace Conformance.Harness;

/// <summary>
/// Issue #143 (ADR-002 conformance harness): the REST half of "the harness HTTP client ...
/// attach[es] a valid token by default". <see cref="Create"/> is a drop-in replacement for
/// <c>new HttpClient()</c> at every existing REST call site (mechanical find-and-replace, no
/// signature/parameter changes needed anywhere) -- the returned client auto-attaches
/// <c>Authorization: Bearer &lt;token&gt;</c> from <see cref="EntraDefaultCredentials"/> on any
/// request that doesn't already carry its own Authorization header (an auth-row test that needs
/// to send a *specific* wrong/missing/malformed token keeps full control simply by setting
/// <see cref="HttpRequestMessage.Headers"/>.Authorization itself, or by using a plain
/// <c>new HttpClient()</c> directly instead of this factory).
/// </summary>
public static class ConformanceHttpClient
{
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers down the chain: the returned HttpClient disposes its handler " +
                        "(disposeHandler: true) and DelegatingHandler.Dispose disposes its inner handler, so " +
                        "the caller's own HttpClient.Dispose releases all three.")]
    public static HttpClient Create() => new(new EntraBearerHandler(new HttpClientHandler()), disposeHandler: true);
}

/// <summary>The <see cref="DelegatingHandler"/> behind <see cref="ConformanceHttpClient.Create"/> --
/// see that type's own doc comment for the full rationale.</summary>
internal sealed class EntraBearerHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null && request.RequestUri is not null)
        {
            string? token = EntraDefaultCredentials.TryGetAccessToken(request.RequestUri);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        return base.SendAsync(request, cancellationToken);
    }
}

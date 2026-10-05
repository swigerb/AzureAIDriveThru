using System.Net;
using System.Text;

namespace SearchIndexIngestor.Tests;

/// <summary>One outbound request's essentials, captured eagerly at send time (method/URL/selected
/// headers/body text) -- NOT the raw <see cref="HttpRequestMessage"/> itself, since production
/// code (<c>using var request = ...</c>) disposes it (and its content stream) immediately after
/// sending, which would make asserting on a captured body unreliable after the fact.</summary>
internal sealed record CapturedRequest(
    HttpMethod Method, string Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>
/// A scriptable, in-memory <see cref="HttpMessageHandler"/> for testing
/// <see cref="SearchIndexHttpClient"/>/<see cref="AzureOpenAiEmbeddingClient"/> without any real
/// network call: records every request it sees, and returns responses from a caller-supplied
/// function (so a test can script a multi-page pagination sequence or an error response).
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];
    private readonly Func<CapturedRequest, HttpResponseMessage> _respond;

    public FakeHttpMessageHandler(Func<CapturedRequest, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    /// <summary>Convenience constructor for a queue of responses returned in order -- the N-th
    /// request gets the N-th response (or the last one, once the queue is exhausted).</summary>
    public static FakeHttpMessageHandler Sequenced(params Func<CapturedRequest, HttpResponseMessage>[] responders)
    {
        var index = 0;
        return new FakeHttpMessageHandler(request =>
        {
            var responder = responders[Math.Min(index, responders.Length - 1)];
            index++;
            return responder(request);
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        // AbsoluteUri (not ToString()) -- Uri.ToString() unescapes some percent-encoded characters
        // (e.g. "%20" back to a literal space) for human-readable display, which would hide a
        // genuine request-building bug (an un-escaped deployment/index name) from a test asserting
        // on the captured URL's exact wire form.
        var captured = new CapturedRequest(request.Method, request.RequestUri!.AbsoluteUri, headers, body);
        Requests.Add(captured);
        return _respond(captured);
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage PlainText(HttpStatusCode statusCode, string text) =>
        new(statusCode) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
}

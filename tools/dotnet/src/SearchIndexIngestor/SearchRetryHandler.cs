using System.Net;

namespace SearchIndexIngestor;

/// <summary>
/// Retries a request against Azure AI Search the same way <c>azure-core</c>'s own
/// <c>RetryPolicy</c> does for every <c>azure-search-documents</c> request (confirmed by reading
/// the pinned <c>azure-core</c> source directly, per Rick's review, item 2b): up to
/// <see cref="RetryTotal"/> attempts total (so up to <c>RetryTotal - 1</c> retries), on
/// 408/429/500/502/503/504, with exponential backoff -- <c>min(BackoffFactor * 2^attempt,
/// BackoffMaxSeconds)</c> -- honouring a numeric <c>Retry-After</c> header when the response has
/// one (azure-core prefers an explicit <c>Retry-After</c> over its own computed backoff).
///
/// Deliberately does NOT add jitter (azure-core's own backoff does, to avoid a retry stampede
/// across many concurrent clients -- not a concern for this single-process CLI tool's own retries,
/// and jitter would make this handler's own backoff non-deterministic to test); this is a
/// documented, intentional simplification, not a parity bug -- the retry COUNT/STATUS-CODE/
/// Retry-After-precedence behaviour this handler exists to pin down is unaffected by jitter.
/// </summary>
internal sealed class SearchRetryHandler : DelegatingHandler
{
    /// <summary>azure-core's own default <c>retry_total</c>.</summary>
    public const int RetryTotal = 10;

    /// <summary>azure-core's own default <c>retry_backoff_factor</c> (seconds).</summary>
    public const double BackoffFactor = 0.8;

    /// <summary>azure-core's own default <c>retry_backoff_max</c> (seconds).</summary>
    public const double BackoffMaxSeconds = 120;

    private static readonly HashSet<HttpStatusCode> RetriableStatusCodes =
    [
        HttpStatusCode.RequestTimeout, // 408
        HttpStatusCode.TooManyRequests, // 429
        HttpStatusCode.InternalServerError, // 500
        HttpStatusCode.BadGateway, // 502
        HttpStatusCode.ServiceUnavailable, // 503
        HttpStatusCode.GatewayTimeout, // 504
    ];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SearchRetryHandler(HttpMessageHandler innerHandler, Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(innerHandler)
    {
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            Exception? transportException = null;
            try
            {
                response = await base.SendAsync(await CloneAsync(request).ConfigureAwait(false), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                transportException = ex;
            }

            var isLastAttempt = attempt >= RetryTotal - 1;
            var shouldRetry = transportException is not null ||
                (response is not null && RetriableStatusCodes.Contains(response.StatusCode));
            if (!shouldRetry || isLastAttempt)
            {
                if (transportException is not null)
                {
                    throw transportException;
                }
                return response!;
            }

            var delay = ComputeDelay(attempt, response);
            response?.Dispose();
            await _delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan ComputeDelay(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter is { Delta: { } delta })
        {
            return delta;
        }
        if (response?.Headers.RetryAfter?.Date is { } date)
        {
            var fromNow = date - DateTimeOffset.UtcNow;
            if (fromNow > TimeSpan.Zero)
            {
                return fromNow;
            }
        }

        var backoffSeconds = Math.Min(BackoffFactor * Math.Pow(2, attempt), BackoffMaxSeconds);
        return TimeSpan.FromSeconds(backoffSeconds);
    }

    /// <summary>A sent <see cref="HttpRequestMessage"/>'s content stream is consumed and the
    /// message itself cannot be resent, so every retry attempt (including the first) sends a fresh
    /// clone with the SAME method/URL/headers/content bytes.</summary>
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }
}

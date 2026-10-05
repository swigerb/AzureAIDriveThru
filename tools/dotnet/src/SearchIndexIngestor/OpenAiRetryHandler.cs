using System.Net;

namespace SearchIndexIngestor;

/// <summary>
/// Retries a request against Azure OpenAI the same way the pinned <c>openai==1.109.1</c> Python
/// SDK's own <c>_base_client.py</c> retries every request (confirmed by reading the pinned SDK's
/// source directly, per Rick's review, item 2b): up to <see cref="MaxRetries"/> retries (so up to
/// <c>MaxRetries + 1</c> attempts total) on 408/409/429/5xx and transport-level connection errors,
/// honouring a <c>Retry-After</c> header (if present and no more than 60 seconds) before falling
/// back to its own exponential backoff -- <c>min(InitialRetryDelaySeconds * 2^attempt,
/// MaxRetryDelaySeconds)</c>.
///
/// Deliberately does NOT reproduce the SDK's own <c>1 - 0.25 * random()</c> jitter multiplier for
/// the same reason <see cref="SearchRetryHandler"/> doesn't: a documented, intentional
/// simplification that leaves this handler's own backoff deterministic and testable, without
/// affecting the retry COUNT/STATUS-CODE/<c>Retry-After</c>-precedence behaviour this handler
/// exists to pin down.
/// </summary>
internal sealed class OpenAiRetryHandler : DelegatingHandler
{
    /// <summary>openai 1.109.1's own default <c>DEFAULT_MAX_RETRIES</c>.</summary>
    public const int MaxRetries = 2;

    // openai 1.109.1's own _base_client.py _calculate_retry_timeout defaults.
    private const double InitialRetryDelaySeconds = 0.5;
    private const double MaxRetryDelaySeconds = 8.0;

    private static readonly HashSet<HttpStatusCode> RetriableStatusCodes =
    [
        HttpStatusCode.RequestTimeout, // 408
        HttpStatusCode.Conflict, // 409
        HttpStatusCode.TooManyRequests, // 429
    ];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OpenAiRetryHandler(HttpMessageHandler innerHandler, Func<TimeSpan, CancellationToken, Task>? delay = null)
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

            var isLastAttempt = attempt >= MaxRetries;
            var shouldRetry = transportException is not null ||
                (response is not null && IsRetriable(response.StatusCode));
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

    private static bool IsRetriable(HttpStatusCode statusCode) =>
        RetriableStatusCodes.Contains(statusCode) || (int)statusCode is >= 500 and <= 599;

    private static TimeSpan ComputeDelay(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter is { Delta: { } delta } && delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(60))
        {
            return delta;
        }
        if (response?.Headers.RetryAfter?.Date is { } date)
        {
            var fromNow = date - DateTimeOffset.UtcNow;
            if (fromNow > TimeSpan.Zero && fromNow <= TimeSpan.FromSeconds(60))
            {
                return fromNow;
            }
        }

        var seconds = Math.Min(InitialRetryDelaySeconds * Math.Pow(2, attempt), MaxRetryDelaySeconds);
        return TimeSpan.FromSeconds(seconds);
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

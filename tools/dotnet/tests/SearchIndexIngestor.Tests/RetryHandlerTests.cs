using System.Net;
using System.Net.Http.Headers;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// C#-only unit tests for <see cref="SearchRetryHandler"/>/<see cref="OpenAiRetryHandler"/> (Rick's
/// review, item 2b/round 2): retry count, which status codes are retried, <c>Retry-After</c>
/// precedence over the computed backoff, retry exhaustion, and transport-level
/// <see cref="HttpRequestException"/> retries. Every test injects a fake <c>delay</c> function so
/// none of these ever sleep for real -- the assertions are on what WOULD have been slept for
/// (recorded delays), and on attempt counts, not wall-clock time.
/// </summary>
public sealed class RetryHandlerTests
{
    private static HttpRequestMessage NewRequest() => new(HttpMethod.Get, "https://fake.example/");

    [Fact]
    public async Task SearchRetryHandler_RetriesOn503_ThenReturnsTheEventualSuccess()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts < 3
                ? FakeHttpMessageHandler.PlainText(HttpStatusCode.ServiceUnavailable, "busy")
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var delays = new List<TimeSpan>();
        using var http = new HttpClient(new SearchRetryHandler(inner, (span, _) => { delays.Add(span); return Task.CompletedTask; }));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
        // min(0.8 * 2^0, 120) = 0.8s, then min(0.8 * 2^1, 120) = 1.6s.
        Assert.Equal([TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.6)], delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task SearchRetryHandler_RetriesEveryAzureCoreRetriableStatusCode(HttpStatusCode statusCode)
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage(statusCode)
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(new SearchRetryHandler(inner, (_, _) => Task.CompletedTask));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SearchRetryHandler_DoesNotRetry_OnANonRetriableStatusCode()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ => { attempts++; return FakeHttpMessageHandler.PlainText(HttpStatusCode.Forbidden, "nope"); });
        using var http = new HttpClient(new SearchRetryHandler(inner, (_, _) => Task.CompletedTask));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task SearchRetryHandler_GivesUp_AfterRetryTotalAttempts()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ => { attempts++; return FakeHttpMessageHandler.PlainText(HttpStatusCode.ServiceUnavailable, "busy"); });
        using var http = new HttpClient(new SearchRetryHandler(inner, (_, _) => Task.CompletedTask));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(SearchRetryHandler.RetryTotal, attempts);
    }

    [Fact]
    public async Task SearchRetryHandler_HonoursRetryAfterDeltaSeconds_OverTheComputedBackoff()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts > 1)
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
            }
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });
        TimeSpan? recordedDelay = null;
        using var http = new HttpClient(new SearchRetryHandler(inner, (span, _) => { recordedDelay = span; return Task.CompletedTask; }));

        await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(42), recordedDelay);
    }

    [Fact]
    public async Task SearchRetryHandler_RetriesOnATransportLevelConnectionError()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("connection reset");
            }
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(new SearchRetryHandler(inner, (_, _) => Task.CompletedTask));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task OpenAiRetryHandler_RetriesOn429_ThenReturnsTheEventualSuccess()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts < 2
                ? FakeHttpMessageHandler.PlainText(HttpStatusCode.TooManyRequests, "slow down")
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var delays = new List<TimeSpan>();
        using var http = new HttpClient(new OpenAiRetryHandler(inner, (span, _) => { delays.Add(span); return Task.CompletedTask; }));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromSeconds(0.5)], delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OpenAiRetryHandler_RetriesEveryOpenAiSdkRetriableStatusCode(HttpStatusCode statusCode)
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage(statusCode)
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(new OpenAiRetryHandler(inner, (_, _) => Task.CompletedTask));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task OpenAiRetryHandler_DoesNotRetry_OnANonRetriableStatusCode()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ => { attempts++; return FakeHttpMessageHandler.PlainText(HttpStatusCode.Unauthorized, "denied"); });
        using var http = new HttpClient(new OpenAiRetryHandler(inner, (_, _) => Task.CompletedTask));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task OpenAiRetryHandler_GivesUp_AfterMaxRetriesPlusOneAttempts()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ => { attempts++; return FakeHttpMessageHandler.PlainText(HttpStatusCode.ServiceUnavailable, "busy"); });
        using var http = new HttpClient(new OpenAiRetryHandler(inner, (_, _) => Task.CompletedTask));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(OpenAiRetryHandler.MaxRetries + 1, attempts);
    }

    [Fact]
    public async Task OpenAiRetryHandler_IgnoresARetryAfterLongerThan60Seconds()
    {
        // Matches the pinned SDK's own guard (_base_client.py): a Retry-After over 60s is NOT
        // honoured, falling back to the computed backoff instead.
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts > 1)
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
            }
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });
        TimeSpan? recordedDelay = null;
        using var http = new HttpClient(new OpenAiRetryHandler(inner, (span, _) => { recordedDelay = span; return Task.CompletedTask; }));

        await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(0.5), recordedDelay);
    }

    [Fact]
    public async Task OpenAiRetryHandler_RetriesOnATransportLevelConnectionError()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("connection reset");
            }
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(new OpenAiRetryHandler(inner, (_, _) => Task.CompletedTask));

        var response = await http.SendAsync(NewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }
}

using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Backend.Tests.Cascade;

/// <summary>Issue #13 Wave 5 (#82): byte-for-byte port tests for rate_limit.py's
/// `parse_retry_hint`/`retry_delay` plus cascade_processor.py's own `_with_rate_limit_retry`
/// ladder (silent retry, then `extension.rate_limited` at attempt 1 with no `final` key, then a
/// final `extension.rate_limited` with `final: true` once retries are exhausted) -- now exercised
/// against <see cref="CascadeRateLimit.WithRetryAsync{T}"/>'s REST-call-wrapping shape rather than
/// the realtime pipeline's own single-notice WS scope cut. The retry-ladder tests use a
/// <see cref="FakeTimeProvider"/> the same way EchoSuppressorThreadSafetyTests does: advancing it
/// synchronously drives any pending `Task.Delay(TimeSpan, TimeProvider, ...)` continuation inline
/// on the calling thread, so no real wall-clock waits or polling are needed.</summary>
public sealed class CascadeRateLimitTests
{
    [Theory]
    [InlineData("Please try again in 1.5s", 1.5)]
    [InlineData("please try again in 250ms", 0.25)]
    [InlineData("Retry after 7 seconds", 7.0)]
    [InlineData("Rate limited. Retry after 2 secs.", 2.0)]
    [InlineData("nothing useful here", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseRetryHint_ParsesSecondsFromFreeText(string? text, double? expected)
    {
        Assert.Equal(expected, CascadeRateLimit.ParseRetryHint(text));
    }

    [Fact]
    public void RetryDelay_UsesDefaultVerbatimWhenNoHint_EvenOutsideBounds()
    {
        // Port fidelity: a null hint returns defaultValue UNCLAMPED (rate_limit.py's own
        // retry_delay) -- only an actual service hint gets clamped to bounds below.
        Assert.Equal(1.5, CascadeRateLimit.RetryDelay(null, 1.5, CascadeRateLimit.FirstRetryBounds));
        Assert.Equal(0.01, CascadeRateLimit.RetryDelay(null, 0.01, CascadeRateLimit.FirstRetryBounds));
    }

    [Theory]
    [InlineData(0.1, 0.5)] // below the first-retry floor -> clamped up
    [InlineData(2.5, 2.5)] // within bounds -> used verbatim
    [InlineData(50.0, 5.0)] // above the first-retry ceiling -> clamped down
    public void RetryDelay_ClampsAnActualHintToBounds(double hint, double expected)
    {
        Assert.Equal(expected, CascadeRateLimit.RetryDelay(hint, 1.5, CascadeRateLimit.FirstRetryBounds));
    }

    [Fact]
    public async Task WithRetryAsync_SucceedsWithoutAnyNoticeWhenTheOperationNeverFails()
    {
        var notices = new List<JsonObject>();
        var settings = new CascadeRateLimitSettings(true, 1.5, 4.0, 2);

        var result = await CascadeRateLimit.WithRetryAsync(
            settings, () => Task.FromResult(42), "test-op",
            (frame, _) => { notices.Add(frame); return Task.CompletedTask; },
            "sess-1", logger: null, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task WithRetryAsync_Attempt0IsSilent_Attempt1NoticeHasNoFinalKey_ThenSucceeds()
    {
        var notices = new List<JsonObject>();
        var settings = new CascadeRateLimitSettings(true, 1.5, 4.0, 2);
        var fakeTime = new FakeTimeProvider();
        var callCount = 0;

        var runTask = CascadeRateLimit.WithRetryAsync(
            settings,
            () =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    throw new FoundryHttpException(429, retryAfterSeconds: null, "rate limited");
                }
                return Task.FromResult("ok");
            },
            "test-op",
            (frame, _) => { notices.Add(frame); return Task.CompletedTask; },
            "sess-1", logger: null, CancellationToken.None, fakeTime);

        // The method runs synchronously up to attempt 0's failure, then suspends in its (silent)
        // first-retry Task.Delay -- the op has been called exactly once so far, with no notice.
        Assert.Equal(1, callCount);
        Assert.Empty(notices);

        // Past the default 1.5s first-retry delay: drives attempt 1 inline, which fails again,
        // sends its (non-final) notice, then suspends in the second-retry Task.Delay.
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, callCount);
        var attempt1Notice = Assert.Single(notices);
        Assert.Equal(CascadeRateLimit.RateLimitedEvent, attempt1Notice["type"]!.GetValue<string>());
        Assert.Equal(1, attempt1Notice["attempt"]!.GetValue<int>());
        Assert.False(attempt1Notice.ContainsKey("final"), "the attempt-1 notice must NOT carry a 'final' key -- only the exhausted ladder's last notice does.");

        // Past the default 4s second-retry delay: drives attempt 2 inline, which succeeds.
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(3, callCount);
        Assert.True(runTask.IsCompletedSuccessfully);
        Assert.Equal("ok", await runTask);
    }

    [Fact]
    public async Task WithRetryAsync_ExhaustsAfterMaxRetries_SendsFinalNoticeAndThrows()
    {
        var notices = new List<JsonObject>();
        var settings = new CascadeRateLimitSettings(true, RetryDelaySeconds: 0.01, SecondRetryDelaySeconds: 0.01, MaxRetries: 1);
        var fakeTime = new FakeTimeProvider();

        var runTask = CascadeRateLimit.WithRetryAsync<object?>(
            settings,
            () => throw new FoundryHttpException(429, retryAfterSeconds: null, "still rate limited"),
            "test-op",
            (frame, _) => { notices.Add(frame); return Task.CompletedTask; },
            "sess-1", logger: null, CancellationToken.None, fakeTime);

        // Attempt 0 fails, suspends in its (tiny, unclamped-default) first-retry delay. Advancing
        // past it drives attempt 1 inline, which immediately exhausts (maxRetries=1) and throws --
        // no further delay is awaited on that path.
        fakeTime.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<CascadeRateLimitExhausted>(() => runTask);
        var finalNotice = Assert.Single(notices);
        Assert.Equal(1, finalNotice["attempt"]!.GetValue<int>());
        Assert.True(finalNotice["final"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WithRetryAsync_NonRateLimitErrorPropagatesUnchangedWithNoNotice()
    {
        var notices = new List<JsonObject>();
        var settings = new CascadeRateLimitSettings(true, 1.5, 4.0, 2);

        var exc = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CascadeRateLimit.WithRetryAsync<object?>(
                settings,
                () => throw new InvalidOperationException("boom"),
                "test-op",
                (frame, _) => { notices.Add(frame); return Task.CompletedTask; },
                "sess-1", logger: null, CancellationToken.None));

        Assert.Equal("boom", exc.Message);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task WithRetryAsync_DisabledSettingsNeverRetries429Either()
    {
        var settings = new CascadeRateLimitSettings(false, 1.5, 4.0, 2);
        var callCount = 0;

        await Assert.ThrowsAsync<FoundryHttpException>(() =>
            CascadeRateLimit.WithRetryAsync<object?>(
                settings,
                () => { callCount++; throw new FoundryHttpException(429, null, "rate limited"); },
                "test-op",
                (_, _) => Task.CompletedTask,
                "sess-1", logger: null, CancellationToken.None));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void FromAppConfig_ReadsResilienceRateLimitSectionDefaults()
    {
        var settings = CascadeRateLimitSettings.FromAppConfig(AppConfig.Load());

        // config.yaml's own resilience.rate_limit block (docs/rate_limit_recovery.md).
        Assert.True(settings.Enabled);
        Assert.Equal(1.5, settings.RetryDelaySeconds);
        Assert.Equal(4.0, settings.SecondRetryDelaySeconds);
        Assert.Equal(2, settings.MaxRetries);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void FromAppConfig_EnvOverrideWinsOverConfigYaml(string envValue, bool expectedEnabled)
    {
        var environment = new Dictionary<string, string> { ["RATE_LIMIT_RECOVERY_ENABLED"] = envValue };
        var settings = CascadeRateLimitSettings.FromAppConfig(AppConfig.Load(), environment);

        Assert.Equal(expectedEnabled, settings.Enabled);
    }
}

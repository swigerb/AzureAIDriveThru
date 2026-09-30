using System.Text.Json.Nodes;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>Byte-for-byte port tests for rate_limit.py's `is_rate_limit_error` /
/// `rate_limit_error_of_response_done`.</summary>
public sealed class RateLimitDetectionTests
{
    [Theory]
    [InlineData("rate_limit_exceeded", null)]
    [InlineData(null, "rate_limit_exceeded")]
    public void IsRateLimitError_TrueWhenCodeOrTypeNamesRateLimit(string? code, string? type)
    {
        var error = new JsonObject { ["code"] = code, ["type"] = type };
        Assert.True(RateLimitDetection.IsRateLimitError(error));
    }

    [Fact]
    public void IsRateLimitError_FalseForOtherErrors()
    {
        var error = new JsonObject { ["code"] = "invalid_value", ["type"] = "invalid_request_error" };
        Assert.False(RateLimitDetection.IsRateLimitError(error));
    }

    [Fact]
    public void IsRateLimitError_FalseForNull()
    {
        Assert.False(RateLimitDetection.IsRateLimitError(null));
    }

    [Fact]
    public void RateLimitErrorOfResponseDone_ReturnsErrorOnlyWhenFailedWithRateLimit()
    {
        var message = new JsonObject
        {
            ["response"] = new JsonObject
            {
                ["status"] = "failed",
                ["status_details"] = new JsonObject
                {
                    ["error"] = new JsonObject { ["code"] = "rate_limit_exceeded" },
                },
            },
        };

        var error = RateLimitDetection.RateLimitErrorOfResponseDone(message);

        Assert.NotNull(error);
        Assert.Equal("rate_limit_exceeded", error!["code"]!.GetValue<string>());
    }

    [Fact]
    public void RateLimitErrorOfResponseDone_NullWhenResponseNotFailed()
    {
        var message = new JsonObject { ["response"] = new JsonObject { ["status"] = "completed" } };

        Assert.Null(RateLimitDetection.RateLimitErrorOfResponseDone(message));
    }

    [Fact]
    public void RateLimitErrorOfResponseDone_NullWhenFailedForAnotherReason()
    {
        var message = new JsonObject
        {
            ["response"] = new JsonObject
            {
                ["status"] = "failed",
                ["status_details"] = new JsonObject
                {
                    ["error"] = new JsonObject { ["code"] = "content_filter" },
                },
            },
        };

        Assert.Null(RateLimitDetection.RateLimitErrorOfResponseDone(message));
    }
}

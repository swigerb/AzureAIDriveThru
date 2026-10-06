using System.Text.Json.Nodes;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rate_limit.py's `is_rate_limit_error` / `rate_limit_error_of_response_done` /
/// `rate_limit_error_of_error_event` -- the pure predicates <see cref="SessionUpdateGuard"/> and
/// <see cref="RateLimitRecovery"/> both need to tell a genuine rate limit apart from any other
/// upstream `error`.
/// </summary>
internal static class RateLimitDetection
{
    /// <summary>True if an error object's `code` or `type` names a rate limit.</summary>
    public static bool IsRateLimitError(JsonObject? error)
    {
        if (error is null)
        {
            return false;
        }
        foreach (var field in new[] { "code", "type" })
        {
            var value = error[field]?.GetValue<string>();
            if (!string.IsNullOrEmpty(value) && value.Contains("rate_limit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The rate-limit error of a failed `response.done`, else null.</summary>
    public static JsonObject? RateLimitErrorOfResponseDone(JsonObject message)
    {
        if (message["response"] is not JsonObject response || response["status"]?.GetValue<string>() != "failed")
        {
            return null;
        }
        var error = (response["status_details"] as JsonObject)?["error"] as JsonObject;
        return IsRateLimitError(error) ? error : null;
    }

    /// <summary>The rate-limit error of an `error` event, else null.</summary>
    public static JsonObject? RateLimitErrorOfErrorEvent(JsonObject message)
    {
        var error = message["error"] as JsonObject;
        return IsRateLimitError(error) ? error : null;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Backend.Auth;

/// <summary>
/// Port of app/backend/app.py's load_app_secret() (docs/dotnet_mapping.md). Reads
/// APP_SESSION_SECRET (a Container App secret in production); if unset, falls back to a
/// per-process random 32-byte key -- fine for a single-replica dev/test run, but every replica
/// would mint tokens no other replica (or backend) can validate, so a missing secret in
/// production is logged as a warning, matching Python's behaviour exactly.
/// </summary>
public static class AppSecretProvider
{
    public const int MinimumRecommendedLength = 32;

    public static byte[] Load(IConfiguration configuration, ILogger logger, bool runningInProduction)
    {
        var configured = configuration["APP_SESSION_SECRET"];
        if (!string.IsNullOrEmpty(configured))
        {
            var secretBytes = Encoding.UTF8.GetBytes(configured);
            if (secretBytes.Length < MinimumRecommendedLength)
            {
                logger.LogWarning(
                    "APP_SESSION_SECRET is only {Length} bytes; recommend at least {Minimum} for HMAC-SHA256.",
                    secretBytes.Length, MinimumRecommendedLength);
            }
            return secretBytes;
        }

        if (runningInProduction)
        {
            logger.LogWarning(
                "APP_SESSION_SECRET is not set. Falling back to a random per-process secret, " +
                "which will invalidate every session token on the next restart or scale-out " +
                "event. Set APP_SESSION_SECRET in production.");
        }

        return RandomNumberGenerator.GetBytes(MinimumRecommendedLength);
    }
}

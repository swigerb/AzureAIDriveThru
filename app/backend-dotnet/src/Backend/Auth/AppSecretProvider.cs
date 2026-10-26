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
internal static class AppSecretProvider
{
    public const int MinimumRecommendedLength = 32;

    public static byte[] Load(IConfiguration configuration, ILogger logger, bool runningInProduction)
    {
        string? configured = configuration["APP_SESSION_SECRET"];
        if (!string.IsNullOrEmpty(configured))
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes(configured);
            if (secretBytes.Length < MinimumRecommendedLength)
            {
                logger.AppSessionSecretTooShort(secretBytes.Length, MinimumRecommendedLength);
            }
            return secretBytes;
        }

        if (runningInProduction)
        {
            logger.AppSessionSecretMissing();
        }

        return RandomNumberGenerator.GetBytes(MinimumRecommendedLength);
    }
}

using System.Text.RegularExpressions;

namespace Backend.Auth;

/// <summary>Byte-for-byte port of app/backend/entra_auth.py's Mode enum (docs/dotnet_mapping.md).</summary>
public enum EntraMode
{
    Entra,
    Development,
}

/// <summary>
/// Thrown for a fail-fast Entra/Development configuration error -- the exact port of
/// entra_auth.py's EntraConfigError. Callers must log the message and exit non-zero before the
/// process ever starts listening (matching every other startup validation failure in Program.cs).
/// </summary>
public sealed class EntraConfigException(string message) : Exception(message);

/// <summary>
/// Byte-for-byte port of app/backend/entra_auth.py's EntraSettings + resolve_settings (ADR-002,
/// issue #147, docs/dotnet_mapping.md). One deliberate, documented deviation: Python's production
/// signal is RUNNING_IN_PRODUCTION; issue #147 requires the .NET port to use
/// ASPNETCORE_ENVIRONMENT=Production (builder.Environment.IsProduction(), which also honours
/// DOTNET_ENVIRONMENT) instead -- every caller here takes that boolean in, already resolved,
/// rather than reading RUNNING_IN_PRODUCTION itself.
/// </summary>
public sealed record EntraSettings(
    EntraMode Mode,
    string? TenantId,
    string? ClientId,
    string ApiScope,
    string AppRole,
    string Instance)
{
    /// <summary>Entra's Development pass-through synthetic principal (entra_auth.py's
    /// SYNTHETIC_PRINCIPAL) -- every request is treated as this oid when AUTH_MODE resolves to
    /// Development. Never a real, dialable Entra object id.</summary>
    public const string SyntheticOid = "00000000-0000-0000-0000-000000000001";

    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";
    private const string DefaultInstance = "https://login.microsoftonline.com/";
    private const string DefaultApiScope = "access_as_user";
    private const string DefaultAppRole = "DriveThru.User";

    private static readonly Regex GuidRegex = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlaceholderWordsRegex = new(
        @"(your[-_]?|placeholder|changeme|example|todo|xxxx+|\bfixme\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AngleOrWhitespaceRegex = new(@"[<>]|\s", RegexOptions.Compiled);

    private static readonly HashSet<string> LoopbackHosts =
        new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "::1", "localhost" };

    /// <summary>entra_auth.py's EntraSettings.issuer: f"{instance}{tenant_id}/v2.0".</summary>
    public string Issuer => $"{Instance}{TenantId}/v2.0";

    /// <summary>entra_auth.py's EntraSettings.discovery_url.</summary>
    public string DiscoveryUrl => $"{Issuer}/.well-known/openid-configuration";

    /// <summary>
    /// entra_auth.py's _is_placeholder: true if the stripped value is empty or the nil GUID, OR
    /// it contains an angle bracket or any whitespace, OR it matches the placeholder-words regex.
    /// </summary>
    private static bool IsPlaceholder(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed == EmptyGuid)
        {
            return true;
        }

        if (AngleOrWhitespaceRegex.IsMatch(trimmed))
        {
            return true;
        }

        return PlaceholderWordsRegex.IsMatch(trimmed);
    }

    /// <summary>
    /// entra_auth.py's validate_entra_ids: checks tenant placeholder, then tenant GUID format,
    /// then client placeholder, then client GUID format, in that exact order, returning the first
    /// human-readable error (or null if both ids are valid).
    /// </summary>
    public static string? ValidateEntraIds(string tenantId, string clientId)
    {
        var tenant = tenantId.Trim();
        var client = clientId.Trim();

        if (IsPlaceholder(tenant))
        {
            return "ENTRA_TENANT_ID is missing or looks like a placeholder value.";
        }

        if (!GuidRegex.IsMatch(tenant))
        {
            return "ENTRA_TENANT_ID is not a valid GUID.";
        }

        if (IsPlaceholder(client))
        {
            return "ENTRA_CLIENT_ID is missing or looks like a placeholder value.";
        }

        if (!GuidRegex.IsMatch(client))
        {
            return "ENTRA_CLIENT_ID is not a valid GUID.";
        }

        return null;
    }

    /// <summary>
    /// entra_auth.py's _validate_instance: accepts https:// (any host) or http:// only to a
    /// loopback host (127.0.0.1, ::1, localhost); normalizes the result to always end with '/'.
    /// </summary>
    private static string ValidateInstance(string raw)
    {
        var value = raw.Trim();
        var isValidHttps = Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isValidLoopbackHttp = parsed is not null
            && string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && LoopbackHosts.Contains(parsed.Host);

        if (!isValidHttps && !isValidLoopbackHttp)
        {
            throw new EntraConfigException(
                $"ENTRA_INSTANCE \"{raw}\" must start with https://, or plain http:// to a loopback host (127.0.0.1, ::1, localhost).");
        }

        return value.EndsWith('/') ? value : value + "/";
    }

    /// <summary>
    /// Byte-for-byte port of entra_auth.py's resolve_settings state machine (ADR-002). Reads
    /// AUTH_MODE/ENTRA_TENANT_ID/ENTRA_CLIENT_ID/ENTRA_INSTANCE/ENTRA_API_SCOPE/ENTRA_APP_ROLE/
    /// APP_SESSION_SECRET via <paramref name="getEnv"/>, and takes <paramref name="isProduction"/>
    /// already resolved by the caller (ASPNETCORE_ENVIRONMENT=Production in .NET, a deliberate,
    /// documented deviation from Python's RUNNING_IN_PRODUCTION -- see the class doc comment).
    /// Throws <see cref="EntraConfigException"/> on any invalid combination; callers must log and
    /// exit non-zero before the process starts listening.
    /// </summary>
    public static EntraSettings Resolve(Func<string, string?> getEnv, bool isProduction)
    {
        var rawMode = (getEnv("AUTH_MODE") ?? string.Empty).Trim();
        var normalizedMode = rawMode.ToLowerInvariant();
        if (normalizedMode is not ("" or "entra" or "development"))
        {
            throw new EntraConfigException($"Unknown AUTH_MODE \"{rawMode}\". Expected \"Entra\" or \"Development\".");
        }

        var tenantId = (getEnv("ENTRA_TENANT_ID") ?? string.Empty).Trim();
        var clientId = (getEnv("ENTRA_CLIENT_ID") ?? string.Empty).Trim();
        var idsBlank = tenantId.Length == 0 && clientId.Length == 0;
        var idsError = idsBlank ? null : ValidateEntraIds(tenantId, clientId);

        EntraMode mode;
        if (normalizedMode == "entra")
        {
            if (idsBlank || idsError is not null)
            {
                var detail = idsError is not null ? $" {idsError}" : string.Empty;
                throw new EntraConfigException(
                    $"AUTH_MODE=Entra requires non-empty, valid ENTRA_TENANT_ID and ENTRA_CLIENT_ID.{detail}");
            }

            mode = EntraMode.Entra;
        }
        else if (normalizedMode == "development")
        {
            if (!idsBlank)
            {
                throw new EntraConfigException(
                    "AUTH_MODE=Development must not be combined with Entra ids (ENTRA_TENANT_ID/ENTRA_CLIENT_ID). " +
                    "Remove them for a real pass-through deployment, or set AUTH_MODE=Entra to use them.");
            }

            if (isProduction)
            {
                throw new EntraConfigException(
                    "AUTH_MODE=Development (and an unset AUTH_MODE) are refused in Production. Set AUTH_MODE=Entra " +
                    "with valid ENTRA_TENANT_ID and ENTRA_CLIENT_ID.");
            }

            mode = EntraMode.Development;
        }
        else
        {
            // AUTH_MODE unset: infer from whether valid Entra ids are present.
            if (idsError is not null)
            {
                throw new EntraConfigException(
                    $"Entra authentication configuration is invalid: {idsError} Set non-empty, valid " +
                    "ENTRA_TENANT_ID and ENTRA_CLIENT_ID for an Entra deployment, or leave both unset for the " +
                    "Development pass-through.");
            }

            if (!idsBlank)
            {
                if (isProduction)
                {
                    throw new EntraConfigException(
                        "Production requires AUTH_MODE=Entra to be set explicitly; Entra ids alone are not enough " +
                        "once ASPNETCORE_ENVIRONMENT=Production.");
                }

                mode = EntraMode.Entra;
            }
            else
            {
                if (isProduction)
                {
                    throw new EntraConfigException(
                        "AUTH_MODE=Development (and an unset AUTH_MODE) are refused in Production. Set " +
                        "AUTH_MODE=Entra with valid ENTRA_TENANT_ID and ENTRA_CLIENT_ID.");
                }

                mode = EntraMode.Development;
            }
        }

        // entra_auth.py checks this AFTER mode resolution -- a separate fail-fast from
        // AppSecretProvider's own warn-only RUNNING_IN_PRODUCTION logic elsewhere in Program.cs.
        if (mode == EntraMode.Entra && isProduction && string.IsNullOrWhiteSpace(getEnv("APP_SESSION_SECRET")))
        {
            throw new EntraConfigException("APP_SESSION_SECRET is required in Production Entra mode.");
        }

        var instanceRaw = getEnv("ENTRA_INSTANCE");
        var instance = ValidateInstance(string.IsNullOrEmpty(instanceRaw) ? DefaultInstance : instanceRaw);

        var apiScopeRaw = getEnv("ENTRA_API_SCOPE");
        var apiScope = string.IsNullOrEmpty(apiScopeRaw) ? DefaultApiScope : apiScopeRaw;

        var appRoleRaw = getEnv("ENTRA_APP_ROLE");
        var appRole = string.IsNullOrEmpty(appRoleRaw) ? DefaultAppRole : appRoleRaw;

        return mode == EntraMode.Development
            ? new EntraSettings(mode, null, null, apiScope, appRole, instance)
            : new EntraSettings(mode, tenantId, clientId, apiScope, appRole, instance);
    }
}

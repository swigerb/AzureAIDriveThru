using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rtmt.py's browser→upstream allow-list (swigerb/SonicAIDriveThru#31,
/// hardened per PR #49 review round 2/5): <c>_CLIENT_ALLOWED_TYPES</c>, <c>_CLIENT_TOP_LEVEL_KEYS</c>,
/// <c>_filter_client_to_server</c>, and <c>_sanitize_turn_detection</c>. Every event a browser is
/// allowed to send upstream, rebuilt from scratch (never the browser's original object) so no
/// forged extra top-level key or sub-key can ride through unfiltered.
/// </summary>
public static class ClientServerFilter
{
    /// <summary>Every event type a browser is allowed to send upstream in production.</summary>
    public static readonly IReadOnlySet<string> ClientAllowedTypes = new HashSet<string>
    {
        "session.update",
        "input_audio_buffer.append",
        "input_audio_buffer.clear",
        "response.cancel",
    };

    /// <summary>Allowed only when CONFORMANCE_TEST_HOOKS=1 (never in a real deployment) -- many
    /// conformance scenarios use a browser-sent response.create as a same-effect stand-in for a
    /// server-VAD-triggered turn.</summary>
    public static readonly IReadOnlySet<string> ClientTestOnlyTypes = new HashSet<string> { "response.create" };

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ClientTopLevelKeys =
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["session.update"] = new HashSet<string> { "type", "event_id", "session" },
            ["input_audio_buffer.append"] = new HashSet<string> { "type", "event_id", "audio" },
            ["input_audio_buffer.clear"] = new HashSet<string> { "type", "event_id" },
            ["response.cancel"] = new HashSet<string> { "type", "event_id", "response_id" },
            ["response.create"] = new HashSet<string> { "type", "event_id" },
        };

    /// <summary>Session keys the browser may legitimately set on session.update -- everything else
    /// GA accepts at the session top level is server-owned and must come only from
    /// <see cref="RealtimeSessionBuilder"/>'s own configuration.</summary>
    public static readonly IReadOnlySet<string> ClientSessionKeys = new HashSet<string>
    {
        "turn_detection", "input_audio_transcription",
    };

    public static readonly IReadOnlySet<string> TurnDetectionAllowedKeys = new HashSet<string>
    {
        "type", "threshold", "prefix_padding_ms", "silence_duration_ms",
    };

    private static readonly Regex EventIdRegex = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex Base64Regex = new("^[A-Za-z0-9+/]*={0,2}$", RegexOptions.Compiled);

    /// <summary>Allow-lists and rebuilds a browser→upstream event. Returns null if the whole event
    /// must be dropped. <paramref name="hooksEnabled"/> mirrors Python's live
    /// <c>conformance_hooks.hooks_enabled_now()</c> re-check (CONFORMANCE_TEST_HOOKS=1).</summary>
    public static JsonObject? Filter(JsonObject message, bool hooksEnabled)
    {
        var msgType = message["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : "";

        var allowed = ClientAllowedTypes.Contains(msgType) || (hooksEnabled && ClientTestOnlyTypes.Contains(msgType));
        if (!allowed || !ClientTopLevelKeys.TryGetValue(msgType, out var allowedKeys))
        {
            return null;
        }

        var filtered = new JsonObject();
        foreach (var (key, value) in message)
        {
            if (allowedKeys.Contains(key))
            {
                filtered[key] = value?.DeepClone();
            }
        }

        if (filtered.TryGetPropertyValue("audio", out var audioNode))
        {
            var audioOk = audioNode is JsonValue av && av.TryGetValue<string>(out var audioStr)
                && Base64Regex.IsMatch(audioStr);
            if (!audioOk)
            {
                return null;
            }
        }

        if (filtered.TryGetPropertyValue("event_id", out var eventIdNode))
        {
            var eventIdOk = eventIdNode is JsonValue ev && ev.TryGetValue<string>(out var eventIdStr)
                && EventIdRegex.IsMatch(eventIdStr);
            if (!eventIdOk)
            {
                filtered.Remove("event_id");
            }
        }

        if (filtered.TryGetPropertyValue("response_id", out var responseIdNode))
        {
            var responseIdOk = responseIdNode is JsonValue rv && rv.TryGetValue<string>(out var responseIdStr)
                && EventIdRegex.IsMatch(responseIdStr);
            if (!responseIdOk)
            {
                return null;
            }
        }

        return filtered;
    }

    /// <summary>Allow-lists a browser-sent turn_detection object down to exactly the four sub-keys
    /// useRealtime.tsx's startSession() ever sends. Returns null (whole object rejected, caller
    /// falls back to the server's own known-good default) unless <c>type == "server_vad"</c>.</summary>
    public static JsonObject? SanitizeTurnDetection(JsonNode? value)
    {
        if (value is not JsonObject obj
            || obj["type"] is not JsonValue typeValue
            || !typeValue.TryGetValue<string>(out var type)
            || type != "server_vad")
        {
            return null;
        }

        var sanitized = new JsonObject { ["type"] = "server_vad" };
        AddBoundedNumber(obj, sanitized, "threshold", 0, 1, intOnly: false);
        AddBoundedNumber(obj, sanitized, "prefix_padding_ms", 0, 5000, intOnly: true);
        AddBoundedNumber(obj, sanitized, "silence_duration_ms", 0, 5000, intOnly: true);
        return sanitized;
    }

    /// <summary>Every middle-tier-authored conversation item id is stamped with this prefix (see
    /// <see cref="MiddleTierItemIds.NewId"/>) -- GA rejects a repeated item id, so every
    /// greeting/tool function_call_output gets a fresh one per call.</summary>
    public const string MiddleTierItemIdPrefix = "sonic_mt_";

    /// <summary>Port of app/backend/rtmt.py's `_drop_from_client`: a server→client
    /// conversation.item.* must never reach the browser if it's a function_call/
    /// function_call_output (tool plumbing, not guest-visible), was authored by the middle tier
    /// itself (greeting/tool-output items, id starts with <see cref="MiddleTierItemIdPrefix"/>),
    /// or has role "system" (the persona's own instructions are never conversation content).</summary>
    public static bool DropFromClient(JsonObject? item)
    {
        if (item is null)
        {
            return false;
        }
        var type = item["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
        if (type is "function_call" or "function_call_output")
        {
            return true;
        }
        var id = item["id"] is JsonValue iv && iv.TryGetValue<string>(out var idStr) ? idStr : null;
        if (id is not null && id.StartsWith(MiddleTierItemIdPrefix, StringComparison.Ordinal))
        {
            return true;
        }
        var role = item["role"] is JsonValue rv && rv.TryGetValue<string>(out var roleStr) ? roleStr : null;
        return role == "system";
    }

    /// <summary>Voices `extension.set_voice` may adopt when `model.allowed_voices` isn't
    /// configured -- the ten GA voices app/frontend/src/lib/voices.ts offers the picker.</summary>
    public static readonly IReadOnlySet<string> DefaultAllowedVoices = new HashSet<string>
    {
        "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar",
    };

    /// <summary>Port of app/backend/rtmt.py's `_sanitize_voice`: returns <paramref name="candidate"/>
    /// if it is a non-empty string present in <paramref name="allowedVoices"/>, else null. Callers
    /// must drop the whole message on null rather than forwarding an unknown value upstream.</summary>
    public static string? SanitizeVoice(string? candidate, IReadOnlySet<string> allowedVoices) =>
        candidate is not null && allowedVoices.Contains(candidate) ? candidate : null;

    private static void AddBoundedNumber(
        JsonObject source, JsonObject target, string key, double lo, double hi, bool intOnly)
    {
        if (!source.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return;
        }
        // Booleans must never be treated as numeric here, even though JsonValue can hold one.
        if (value.TryGetValue<bool>(out _))
        {
            return;
        }
        double numeric;
        if (intOnly)
        {
            if (!value.TryGetValue<int>(out var intValue))
            {
                return;
            }
            numeric = intValue;
        }
        else if (!value.TryGetValue<double>(out numeric))
        {
            return;
        }
        if (numeric >= lo && numeric <= hi)
        {
            target[key] = intOnly ? JsonValue.Create((int)numeric) : JsonValue.Create(numeric);
        }
    }
}

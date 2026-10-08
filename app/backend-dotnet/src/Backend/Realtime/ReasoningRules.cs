using System.Text.RegularExpressions;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rtmt.py's `deployment_supports_reasoning`, `parse_reasoning_model`,
/// `normalize_reasoning_effort` (docs/persona-architecture.md section 7.4, issue #13's explicitly
/// named "reasoning parsing" pure-function acceptance target) plus the reasoning-precedence rule
/// itself (`RTMiddleTier._reasoning_model`/`reasoning_enabled`).
///
/// Precedence for whether reasoning-model-only fields (`reasoning`, `parallel_tool_calls`) may be
/// sent upstream at all, highest first:
/// 1. A runtime rejection already happened on this process (<see cref="RealtimeSessionConfig.ReasoningRejected"/>) -- always wins, forces false.
/// 2. The explicit `reasoning_model` operator kill-switch (true/false) -- overrides everything else.
/// 3. `reasoningOverride` (#75: THIS session's own bound model's catalog `reasoning` flag).
/// 4. The deployment-name heuristic (<see cref="DeploymentSupportsReasoning"/>) -- last resort,
///    used only when the switch is "auto" (null) and no catalog override was passed.
/// </summary>
internal static class ReasoningRules
{
    /// <summary>Values accepted by gpt-realtime-2.1 for `reasoning.effort` (probed live 2026-09-22).</summary>
    public static readonly IReadOnlySet<string> ReasoningEfforts = new HashSet<string>
    {
        "none", "minimal", "low", "medium", "high", "xhigh",
    };

    /// <summary>Config values that mean "do not send `reasoning` at all".</summary>
    private static readonly IReadOnlySet<string> ReasoningDisabledValues = new HashSet<string>
    {
        "", "off", "disabled", "false", "null",
    };

    /// <summary>Realtime model families that are NOT reasoning models. gpt-realtime-1.5 answers
    /// `reasoning` (any effort, even "none") and `parallel_tool_calls: true` with `invalid_value`
    /// "Unsupported option for this model" -- and drops the whole session.update, tools included.
    /// The dated `gpt-realtime-2025-08-28` snapshot is the original non-reasoning gpt-realtime,
    /// not gpt-realtime-2.</summary>
    private static readonly Regex NonReasoningDeploymentRe = new(
        @"^(gpt-4o.*|gpt-realtime(-mini.*|-1(\.\d+)?(-.*)?|-\d{4}-\d{2}-\d{2})?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Best-effort check from the deployment name, used only when `model.reasoning_model` is
    /// "auto". azd names deployments after the model, so a rollback to `gpt-realtime-1.5` is
    /// recognised. Unrecognised custom names are assumed to support reasoning; if they don't, the
    /// rejected session.update is caught by the fallback and reasoning is switched off for the
    /// rest of the process.
    /// </summary>
    public static bool DeploymentSupportsReasoning(string? deployment)
    {
        if (string.IsNullOrWhiteSpace(deployment))
        {
            return true;
        }
        return !NonReasoningDeploymentRe.IsMatch(deployment.Trim());
    }

    /// <summary>
    /// `model.reasoning_model` / AZURE_OPENAI_REALTIME_REASONING_MODEL: true/false force it; null
    /// ("auto", empty, unknown) infers it from the deployment name.
    /// </summary>
    public static bool? ParseReasoningModel(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        if (text is "true" or "yes" or "on" or "1")
        {
            return true;
        }
        if (text is "false" or "no" or "off" or "0")
        {
            return false;
        }
        // Unlike ParseReasoningModel(string?), an unrecognised value here is only logged by the
        // caller (this pure function has no logger dependency, mirroring the other pure ports in
        // this file) -- see RealtimeProcessor for the log call.
        return null;
    }

    /// <summary>True if <paramref name="value"/> was recognised as true/false/auto -- distinguishes
    /// "explicitly auto/empty" from "unrecognised, should be logged as ignored" the same way
    /// Python's `parse_reasoning_model` does inline.</summary>
    public static bool IsRecognisedReasoningModelValue(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        return text is "" or "auto" or "null" or "none" or "true" or "yes" or "on" or "1"
            or "false" or "no" or "off" or "0";
    }

    /// <summary>
    /// Maps a configured effort to the wire value, or null to omit `reasoning` entirely. Empty /
    /// "off" / "disabled" omit the field. "none" is a real effort level on gpt-realtime-2.1 (no
    /// reasoning tokens) and is sent as-is.
    /// </summary>
    public static string? NormalizeReasoningEffort(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var effort = value.Trim().ToLowerInvariant();
        if (ReasoningDisabledValues.Contains(effort))
        {
            return null;
        }
        if (!ReasoningEfforts.Contains(effort))
        {
            return null;
        }
        return effort;
    }

    /// <summary>True if <paramref name="value"/> was recognised (a real effort level, one of the
    /// disabled-values aliases, or null/omitted) -- distinguishes "silently disabled" from
    /// "unrecognised, should be logged as ignored" the same way Python's
    /// `normalize_reasoning_effort` does inline (its `value is None` early-return never reaches
    /// the "unknown" warning).</summary>
    public static bool IsRecognisedReasoningEffort(string? value) =>
        value is null || ReasoningDisabledValues.Contains(value.Trim().ToLowerInvariant())
            || ReasoningEfforts.Contains(value.Trim().ToLowerInvariant());
}

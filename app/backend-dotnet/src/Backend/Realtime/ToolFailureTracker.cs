using System.Text.Json.Nodes;
using Backend.Prompts;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rtmt.py's <c>_ToolFailureTracker</c> (issue #13 Wave 4,
/// swigerb/SonicAIDriveThru#36, PR #58 re-review "S1"/"S2") -- counts consecutive *failed tool
/// rounds* on one connection, not failed calls or lack of success. The earlier per-call,
/// reset-on-any-success design let a model loop <c>update_order</c> (fails) -&gt; <c>get_order</c>
/// (succeeds -- the very call our own error text tells it to make) -&gt; <c>update_order</c>
/// (fails) -&gt; ... forever with no guest input, since each <c>get_order</c> success reset the
/// count back to zero.
///
/// Once <see cref="FailureCap"/> is reached, <see cref="ConsumeCapNotice"/> grants exactly ONE
/// server-authored, tool-free <c>response.create</c> (<see cref="ToolFailureCapNotice"/>) so the
/// model can apologise out loud -- but every <c>response.done</c> after that, while still at cap
/// with no guest turn in between, goes back to sending nothing at all. A model (or, in a
/// deterministic conformance script, a test) that keeps calling tools every round regardless of
/// what <c>tool_choice</c> said must not be able to ride an unbounded ladder of one-more-apology
/// responses with zero guest input -- only a single apology per capped streak.
/// </summary>
internal sealed class ToolFailureTracker
{
    /// <summary>Mirrors rtmt.py's module-level <c>_TOOL_FAILURE_CAP</c>: after this many
    /// *consecutive* failed tool rounds with no guest turn in between, stop auto-continuing the
    /// model.</summary>
    public const int FailureCap = 2;

    public int Count { get; private set; }

    private bool _roundHadFailure;
    private bool _capNoticeSent;

    /// <summary>One tool call in the current round raised an unhandled exception. Marks the
    /// round as failed; does not touch <see cref="Count"/> yet -- <see cref="EndRound"/> does
    /// that once per round, so several parallel failing calls in one round (e.g. two tool calls
    /// in the same response, both raising) still only count as a single failed round.</summary>
    public void RecordCallFailure() => _roundHadFailure = true;

    /// <summary>Call once per <c>response.done</c> that had &gt;=1 tool call pending. Increments
    /// the streak only if at least one call in this round failed. A round with only successful
    /// calls (e.g. the <c>get_order</c> the model's own error text tells it to make after a
    /// failure) leaves the streak unchanged -- it must NOT reset it back to zero, or the cap
    /// could never be reached no matter how long the loop runs.</summary>
    public void EndRound()
    {
        if (_roundHadFailure)
        {
            Count++;
            _roundHadFailure = false;
        }
    }

    /// <summary>Genuine guest activity (speech_started / a completed input transcription) breaks
    /// the streak -- only the guest, not the model retrying tools on its own, gets to start the
    /// count over.</summary>
    public void ResetForNewTurn()
    {
        Count = 0;
        _roundHadFailure = false;
        _capNoticeSent = false;
    }

    public bool AtCap() => Count >= FailureCap;

    /// <summary>True (and marks the notice sent) the first time this is called after the cap is
    /// reached; false every time after that, until <see cref="ResetForNewTurn"/> runs. Lets the
    /// <c>response.done</c> handler send exactly one <c>tool_choice="none"</c> apology per capped
    /// streak, then fall back to sending nothing at all for as long as the streak continues with
    /// no guest turn -- see the class doc.</summary>
    public bool ConsumeCapNotice()
    {
        if (_capNoticeSent)
        {
            return false;
        }
        _capNoticeSent = true;
        return true;
    }
}

/// <summary>
/// Port of app/backend/rtmt.py's module-level <c>_TOOL_FAILURE_CAP_INSTRUCTIONS_FALLBACK</c>,
/// <c>_tool_failure_cap_instructions()</c>, and <c>_build_tool_failure_cap_notice_msg()</c> --
/// the server-authored <c>response.create</c> sent once per capped failure streak (issue #13
/// Wave 4, swigerb/SonicAIDriveThru#36, PR #58 re-review "S1").
/// </summary>
internal static class ToolFailureCapNotice
{
    /// <summary>The brand prompt config's optional <c>error_messages.yaml</c> key for this
    /// notice's <c>instructions</c> text.</summary>
    private const string InstructionsKey = "tool_failure_cap_instructions";

    /// <summary>Mirrors rtmt.py's <c>_TOOL_FAILURE_CAP_INSTRUCTIONS_FALLBACK</c> byte-for-byte --
    /// used only when no persona pack configures <see cref="InstructionsKey"/>.</summary>
    public const string InstructionsFallback =
        "The order system just failed twice in a row and nothing was added or changed. " +
        "Do not say an item was added, removed, or changed. Briefly apologise, say you " +
        "couldn't update the order just now, and ask the guest to repeat what they'd like.";

    /// <summary>Return the response-level <c>instructions</c> text for the tool-failure cap
    /// notice. Reads the brand prompt config's <c>error_messages.yaml</c> key
    /// <c>tool_failure_cap_instructions</c> if one is configured; otherwise returns
    /// <see cref="InstructionsFallback"/>. Deliberately checks <see cref="PromptLoader.ErrorMessages"/>
    /// directly rather than calling <see cref="PromptLoader.RenderError"/> unconditionally --
    /// <c>RenderError</c>'s own fallback for an unknown key is a generic "An error occurred
    /// (...)" placeholder, not this method's neutral default, and a placeholder string sent to
    /// the model as its ONLY instructions for this response would be worse than nothing.</summary>
    public static string ResolveInstructions(PromptLoader? promptLoader) =>
        promptLoader is not null && promptLoader.ErrorMessages.ContainsKey(InstructionsKey)
            ? promptLoader.RenderError(InstructionsKey)
            : InstructionsFallback;

    /// <summary>Build the server-authored <c>response.create</c> sent once per capped failure
    /// streak. <c>response.tool_choice: "none"</c> stops the model from calling a tool again with
    /// no guest input; <c>response.instructions</c> (see <see cref="ResolveInstructions"/> above)
    /// stops it from falsely claiming the order changed anyway, on top of not calling a tool.
    /// This frame is server-authored (never derived from browser input), so the #31
    /// browser-&gt;upstream allow-list in <c>ClientServerFilter</c> is unaffected.</summary>
    public static string BuildMessage(PromptLoader? promptLoader)
    {
        var json = new JsonObject
        {
            ["type"] = "response.create",
            ["response"] = new JsonObject
            {
                ["tool_choice"] = "none",
                ["instructions"] = ResolveInstructions(promptLoader),
            },
        };
        return json.ToJsonString();
    }
}

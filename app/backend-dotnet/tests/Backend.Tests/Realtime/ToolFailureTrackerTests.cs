using System.Text.Json;
using Backend.Prompts;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>
/// Issue #13 Wave 4 (swigerb/SonicAIDriveThru#36, PR #58 re-review "S1"/"S2"): byte-for-byte-
/// behaviour port tests for app/backend/rtmt.py's <c>_ToolFailureTracker</c>,
/// <c>_tool_failure_cap_instructions</c>, and <c>_build_tool_failure_cap_notice_msg</c>, exercised
/// directly against <see cref="ToolFailureTracker"/>/<see cref="ToolFailureCapNotice"/> (no
/// <see cref="Backend.Sessions.RealtimeProcessor"/>/live sockets needed -- same isolation
/// technique as <c>RateLimitRecoveryUnitTests</c>). The black-box, end-to-end version of this
/// same contract lives in the conformance suite's
/// <c>Conformance.Tests.Scenarios.Ordering.ToolFailureCapAndTicketRefreshTests</c>.
/// </summary>
public sealed class ToolFailureTrackerTests
{
    // ── round-based counting, not call-based or success-reset ───────────────────────────────────

    [Fact]
    public void NewTracker_IsNotAtCap()
    {
        var tracker = new ToolFailureTracker();

        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.AtCap());
    }

    [Fact]
    public void EndRound_WithNoFailureRecorded_DoesNotIncrementCount()
    {
        var tracker = new ToolFailureTracker();

        tracker.EndRound();

        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.AtCap());
    }

    [Fact]
    public void SeveralFailingCallsInOneRound_CountAsOnlyOneFailedRound()
    {
        var tracker = new ToolFailureTracker();

        // Two (or more) parallel tool calls in the same round, both raising.
        tracker.RecordCallFailure();
        tracker.RecordCallFailure();
        tracker.EndRound();

        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void OneFailedRound_IsNotAtCap()
    {
        var tracker = new ToolFailureTracker();

        tracker.RecordCallFailure();
        tracker.EndRound();

        Assert.Equal(1, tracker.Count);
        Assert.False(tracker.AtCap());
    }

    [Fact]
    public void TwoConsecutiveFailedRounds_ReachesTheCap()
    {
        var tracker = new ToolFailureTracker();

        tracker.RecordCallFailure();
        tracker.EndRound();
        tracker.RecordCallFailure();
        tracker.EndRound();

        Assert.Equal(2, tracker.Count);
        Assert.True(tracker.AtCap());
    }

    /// <summary>PR #58 re-review "S1" repro: the earlier per-call, reset-on-any-success design
    /// let a model loop update_order (fails) -&gt; get_order (succeeds) -&gt; update_order (fails)
    /// -&gt; ... forever, because each success reset the streak to zero. A round with ONLY
    /// successful calls (no RecordCallFailure() at all) must leave the streak unchanged, never
    /// reset it.</summary>
    [Fact]
    public void SuccessfulRound_DoesNotResetAnExistingStreak()
    {
        var tracker = new ToolFailureTracker();

        tracker.RecordCallFailure();
        tracker.EndRound(); // count = 1

        tracker.EndRound(); // a round with only successes -- no RecordCallFailure() call

        Assert.Equal(1, tracker.Count);

        tracker.RecordCallFailure();
        tracker.EndRound(); // count = 2 -- the cap is still reachable

        Assert.True(tracker.AtCap());
    }

    // ── one-shot cap notice ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConsumeCapNotice_BeforeCap_StillReturnsTrueOnce()
    {
        // ConsumeCapNotice() itself has no cap gate -- callers only invoke it after checking
        // AtCap(), mirroring rtmt.py's `if tool_failures.at_cap(): if
        // tool_failures.consume_cap_notice():` nesting. This test documents that the one-shot
        // gate is independent state, not derived from Count.
        var tracker = new ToolFailureTracker();

        Assert.True(tracker.ConsumeCapNotice());
        Assert.False(tracker.ConsumeCapNotice());
    }

    [Fact]
    public void ConsumeCapNotice_AtCap_ReturnsTrueOnceThenFalse()
    {
        var tracker = new ToolFailureTracker();
        tracker.RecordCallFailure();
        tracker.EndRound();
        tracker.RecordCallFailure();
        tracker.EndRound();
        Assert.True(tracker.AtCap());

        Assert.True(tracker.ConsumeCapNotice());
        Assert.False(tracker.ConsumeCapNotice());
        Assert.False(tracker.ConsumeCapNotice());
    }

    [Fact]
    public void FurtherFailedRoundsPastTheCap_NeverReopenTheNotice()
    {
        var tracker = new ToolFailureTracker();
        tracker.RecordCallFailure();
        tracker.EndRound();
        tracker.RecordCallFailure();
        tracker.EndRound();
        Assert.True(tracker.ConsumeCapNotice());

        // A third, fourth, ... failed round with still no guest turn -- the notice stays consumed.
        tracker.RecordCallFailure();
        tracker.EndRound();

        Assert.Equal(3, tracker.Count);
        Assert.True(tracker.AtCap());
        Assert.False(tracker.ConsumeCapNotice());
    }

    // ── guest-turn reset ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResetForNewTurn_AfterCap_ClearsCountAndReopensTheNotice()
    {
        var tracker = new ToolFailureTracker();
        tracker.RecordCallFailure();
        tracker.EndRound();
        tracker.RecordCallFailure();
        tracker.EndRound();
        Assert.True(tracker.ConsumeCapNotice());

        tracker.ResetForNewTurn();

        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.AtCap());
        // A fresh streak after the reset must be able to earn its own single notice again.
        tracker.RecordCallFailure();
        tracker.EndRound();
        tracker.RecordCallFailure();
        tracker.EndRound();
        Assert.True(tracker.AtCap());
        Assert.True(tracker.ConsumeCapNotice());
    }

    [Fact]
    public void ResetForNewTurn_MidRound_AlsoClearsAnInFlightRoundHadFailureFlag()
    {
        var tracker = new ToolFailureTracker();
        tracker.RecordCallFailure(); // round in progress, not yet ended

        tracker.ResetForNewTurn();
        tracker.EndRound(); // the in-flight failure must not retroactively count

        Assert.Equal(0, tracker.Count);
    }

    // ── ToolFailureCapNotice: instructions resolution and message shape ─────────────────────────

    [Fact]
    public void ResolveInstructions_NullPromptLoader_ReturnsFallback()
    {
        Assert.Equal(ToolFailureCapNotice.InstructionsFallback, ToolFailureCapNotice.ResolveInstructions(null));
    }

    [Fact]
    public void ResolveInstructions_PromptLoaderWithoutTheKey_ReturnsFallback_NotRenderErrorsPlaceholder()
    {
        using var pack = new MinimalPromptPackFixture();
        var loader = new PromptLoader(pack.PersonasDir, "acme");

        var instructions = ToolFailureCapNotice.ResolveInstructions(loader);

        // Must be the neutral fallback, NOT PromptLoader.RenderError's own
        // "An error occurred (tool_failure_cap_instructions)." placeholder for an unknown key --
        // see ToolFailureCapNotice.ResolveInstructions's own doc for why that placeholder would be
        // a worse response.instructions value than the neutral default.
        Assert.Equal(ToolFailureCapNotice.InstructionsFallback, instructions);
        Assert.DoesNotContain("An error occurred", instructions);
    }

    [Fact]
    public void ResolveInstructions_PromptLoaderWithTheKeyConfigured_UsesTheBrandSpecificText()
    {
        using var pack = new MinimalPromptPackFixture();
        pack.AppendErrorMessage("acme", "tool_failure_cap_instructions",
            "Say sorry, carhop-style, and ask the guest to repeat their order.");
        var loader = new PromptLoader(pack.PersonasDir, "acme");

        var instructions = ToolFailureCapNotice.ResolveInstructions(loader);

        Assert.Equal("Say sorry, carhop-style, and ask the guest to repeat their order.", instructions);
    }

    [Fact]
    public void BuildMessage_IsAServerAuthoredResponseCreateWithToolChoiceNoneAndNonEmptyInstructions()
    {
        var json = ToolFailureCapNotice.BuildMessage(promptLoader: null);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("response.create", root.GetProperty("type").GetString());
        var response = root.GetProperty("response");
        Assert.Equal("none", response.GetProperty("tool_choice").GetString());
        var instructions = response.GetProperty("instructions").GetString();
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Equal(ToolFailureCapNotice.InstructionsFallback, instructions);
    }

    /// <summary>Minimal valid persona prompt pack, just enough for <see cref="PromptLoader"/>'s
    /// constructor to succeed -- same minimal shape as PromptLoaderTests's own
    /// WriteValidPromptPack, kept local here since this suite only ever needs the
    /// error_messages.yaml half of it.</summary>
    private sealed class MinimalPromptPackFixture : IDisposable
    {
        public string PersonasDir { get; }

        public MinimalPromptPackFixture()
        {
            PersonasDir = Path.Combine(Path.GetTempPath(), "unity-tool-failure-cap-tests-" + Guid.NewGuid().ToString("n"));
            var promptsDir = Path.Combine(PersonasDir, "acme", "prompts");
            Directory.CreateDirectory(promptsDir);

            File.WriteAllText(Path.Combine(promptsDir, "system_prompt.yaml"), """
                sections:
                  - priority: 1
                    content: "Welcome to Acme."
                """);
            File.WriteAllText(Path.Combine(promptsDir, "greeting.yaml"), """
                greeting:
                  type: "text"
                  text: "Welcome!"
                """);
            File.WriteAllText(Path.Combine(promptsDir, "tool_schemas.yaml"), """
                tools:
                  - name: "add_item"
                    type: "function"
                """);
            File.WriteAllText(Path.Combine(promptsDir, "error_messages.yaml"), """
                messages:
                  generic_error: "Something went wrong."
                  item_not_on_menu: "Sorry, that isn't on our menu."
                  size_not_available: "Sorry, that size isn't available."
                  item_not_in_order: "That isn't in the order."
                  machine_unavailable: "Sorry, that isn't available right now."
                  extras_blocked_category: "Extras can't be added to that category right now."
                  extras_no_base_item: "Extras need a base item in the order first."
                  item_out_of_mode: "Sorry, that isn't on the menu right now."
                """);
            File.WriteAllText(Path.Combine(promptsDir, "hints.yaml"), """
                hints:
                  upsell: "Would you like fries with that?"
                """);
        }

        /// <summary>Appends one more <c>key: "value"</c> line to this pack's
        /// error_messages.yaml <c>messages</c> mapping.</summary>
        public void AppendErrorMessage(string personaId, string key, string value)
        {
            var path = Path.Combine(PersonasDir, personaId, "prompts", "error_messages.yaml");
            File.AppendAllText(path, $"\n  {key}: \"{value}\"\n");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(PersonasDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}

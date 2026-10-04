using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// Issue #82: the dedicated backend process the cascade pipeline's conformance rows need.
/// Unlike every other <see cref="ConformanceFixture"/> derivative so far, this one needs a THIRD
/// fake upstream -- <see cref="Chat"/>, a <see cref="FakeChatCompletionsServer"/> standing in for
/// the Azure AI Foundry Model Inference chat-completions endpoint -- alongside the two
/// <see cref="ConformanceFixture"/> already starts. STT/TTS do NOT need a fourth fake: the design
/// deliberately routes cascade audio through the SAME `AZURE_OPENAI_EASTUS2_ENDPOINT` account the
/// realtime pipeline already uses (see `app.py`'s `audio_endpoint=llm_endpoint`), so
/// <see cref="ConformanceFixture.Realtime"/> itself already serves
/// `/openai/v1/audio/transcriptions` and `/openai/v1/audio/speech` (see that class's own
/// `ExpectedCascadeBearerToken` doc comment) with zero extra process needed.
///
/// Uses the real, shipped `sonic` persona (no persona override, unlike
/// <see cref="ModelSelectionConformanceFixture"/>) -- `personas/sonic/persona.json` declares
/// `models.cascade: {default: gpt-5-mini, allowed: [gpt-5-mini]}` (#82's own catalog entries),
/// so no test-only fixture pack is needed to exercise cascade dispatch, tool calling, or
/// pricing against a real deployment-shaped persona. Per Rick's #118 review item 3, `phi-4`
/// stays in the model catalog (see <see cref="PhiChatDeployment"/> below, kept only so a future
/// fixture override can still exercise catalog dispatch) but was pulled from every persona's
/// cascade allow-list until it's qualified for live tool calling in #87.
/// </summary>
public sealed class CascadeConformanceFixture : ConformanceFixture
{
    /// <summary>The bearer token every fake in this fixture (chat AND audio) requires -- proves
    /// `CascadeProcessor`'s credential (real `DefaultAzureCredential`, substituted in-process by
    /// `conformance_hooks.cascade_credential()` for this harness -- see that function's own
    /// docstring) actually reaches every one of its three upstream calls, not just one.</summary>
    public const string BearerToken = "cascade-conformance-fake-bearer-token";

    public const string ChatDeployment = "gpt-5-mini-cascade-conformance";
    public const string PhiChatDeployment = "phi-4-cascade-conformance";
    public const string TranscriptionDeployment = "gpt-4o-transcribe-cascade-conformance";
    public const string TtsDeployment = "gpt-4o-mini-tts-cascade-conformance";

    public FakeChatCompletionsServer Chat { get; } = new();

    /// <summary>The idle-timeout/nudge/first-frame/greeting-timeout knobs on
    /// <see cref="BackendProfiles.ShortTimers"/> apply generically at the session-lifecycle layer
    /// (not gated to the realtime pipeline the way this fixture's doc comment previously assumed
    /// -- confirmed the hard way: a 1-second idle budget force-closed cascade sessions mid-test
    /// whenever a scenario needed more than ~1s of client-side inactivity, e.g. the barge-in
    /// row's deliberately-slow scripted turn and its own polling wait). Only the rate-limit retry
    /// delay overrides (0.2s/0.4s instead of config.yaml's 1.5s/4.0s default) are actually needed
    /// here, so this fixture uses <see cref="BackendProfiles.RateLimitTimers"/> instead -- the
    /// same profile the realtime pipeline's own RateLimitRetryTimingTests use for the identical
    /// reason -- which keeps idle/grace/nudge/first-frame comfortably long (10s/10s/10s/3s) while
    /// still shortening the two rate-limit knobs
    /// `Cascade_a_429_from_chat_completion_notifies_the_client_then_completes_once_it_resolves`
    /// (and any future cascade rate-limit timing test) needs.</summary>
    protected override BackendProfile Profile => BackendProfiles.RateLimitTimers;

    protected override async Task<IReadOnlyDictionary<string, string>> StartExtraFakesAsync()
    {
        Chat.ExpectedBearerToken = BearerToken;
        await Chat.StartAsync().ConfigureAwait(false);
        Realtime.ExpectedCascadeBearerToken = BearerToken;

        return new Dictionary<string, string>
        {
            ["AZURE_AI_FOUNDRY_ENDPOINT"] = Chat.BaseUri.ToString().TrimEnd('/'),
            ["AZURE_AI_MODEL_DEPLOYMENTS"] =
                $$"""
                {"gpt-5-mini":"{{ChatDeployment}}","phi-4":"{{PhiChatDeployment}}","gpt-4o-transcribe":"{{TranscriptionDeployment}}","gpt-4o-mini-tts":"{{TtsDeployment}}"}
                """,
            // conformance_hooks.cascade_credential() requires BOTH CONFORMANCE_TEST_HOOKS=1
            // (already set for every fixture -- see BackendEnvironment.cs) AND this var to
            // substitute its fake, static-token credential; either alone leaves the real
            // DefaultAzureCredential in place, which would hang/fail with no real Azure AD
            // identity available in CI.
            ["CONFORMANCE_CASCADE_FAKE_TOKEN"] = BearerToken,

            // Widens RateLimitTimers' own 10s idle/grace/nudge budget to 45s for THIS fixture
            // only (StartExtraFakesAsync's dict wins over Profile.ExtraEnvironment on a key
            // collision -- see ConformanceFixture's own doc comment). session_manager.py's idle
            // clock is driven purely by INCOMING guest activity (`touch_activity`,
            // never by outgoing server frames -- confirmed against test_order_resume.py's own
            // "silent mic audio reset the idle clock"/"the rate-limit retry counted as guest
            // activity" cases), so a turn that is genuinely still in flight (a real HTTP
            // round trip to the fakes, not a hang) but simply takes a while under a loaded CI/dev
            // machine can otherwise race the idle-checker into force-closing the socket out from
            // under it before the response ever arrives -- reproduced directly: the barge-in
            // row's cancel-then-immediately-re-answer sequence (a real network round trip with no
            // further client audio in between) was observed being killed by "Closing idle session
            // ... (idle > 10s)" under machine load in ~2 of 6 back-to-back local runs, even though
            // every step it depends on (request landing, cancellation, the second completions
            // call) had already succeeded. 45s keeps comfortably clear of that race without
            // weakening what any row here actually proves (none of these tests assert on the
            // idle timeout itself -- that is IdleTimeoutTests'/RateLimitRecoveryTests' own job on
            // the realtime pipeline, per RateLimitTimers' own doc comment).
            ["CONFORMANCE_IDLE_TIMEOUT_SECONDS"] = "45",
            ["CONFORMANCE_GRACE_SECONDS"] = "45",
            ["CONFORMANCE_NUDGE_AFTER_SECONDS"] = "45",
        };
    }

    protected override async Task StopExtraFakesAsync() => await Chat.DisposeAsync().ConfigureAwait(false);

    // Rick's PR #253 review item 2 (follow-up): this fixture's 7 scenarios (including the
    // barge-in row that actually causes the FIFO leak -- see
    // FakeChatCompletionsServer.AssertNoPendingScriptedResponses's own doc comment for the exact
    // mechanism) never called AssertNoPendingScriptedResponses by hand, so a leak here would
    // have gone uncaught entirely. Wiring it through the base class's own post-body hook instead
    // of a hand-placed call in every scenario means every CURRENT and FUTURE scenario in this
    // fixture is covered automatically, and a leak fails the scenario that actually caused it.
    protected override void AssertNoPendingExtraFakeState() => Chat.AssertNoPendingScriptedResponses();
}

[CollectionDefinition(Name)]
public sealed class CascadeConformanceCollection : ICollectionFixture<CascadeConformanceFixture>
{
    public const string Name = "ConformanceCascade";
}

using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// #248 (Python cascade parity: per-persona default voice, `?mode=` daypart support): the
/// cascade pipeline's own menu-mode/voice conformance rows need BOTH <see cref="CascadeConformanceFixture"/>'s
/// three-fake wiring (chat completions + the shared realtime-upstream fake for STT/TTS -- see
/// that class's own doc comment for why there is no fourth fake) AND a persona override to
/// `test-delta` (`features.dayparts: true`, `voice.default: "alloy"`, a breakfast/lunch meal
/// pair sharing meal number 2 -- the SAME fixture pack <see cref="MenuModeConformanceFixture"/>
/// already proved out on the realtime pipeline). `CascadeConformanceFixture` itself is sealed and
/// hard-codes a real production persona pack (every real pack's voice is "marin", identical to
/// the deployment-wide default, which makes it useless for proving per-persona voice lookup is
/// actually happening -- see `_resolve_persona_voice`'s own test doc comment on the Python side),
/// so this is a new, separate fixture/collection rather than a subclass of it.
///
/// `test-delta/persona.json` did not declare a `models.cascade` block before #248 -- it now does
/// (`{"default": "gpt-5-mini", "allowed": ["gpt-5-mini"]}`, identical to the real production
/// packs' own entries), added specifically to unlock this fixture without a brand-new
/// fixture-only pack.
/// </summary>
public sealed class CascadeMenuModeAndVoiceConformanceFixture : ConformanceFixture
{
    public const string Persona_ = "test-delta";

    public const string ChatDeployment = "gpt-5-mini-cascade-menu-mode-conformance";
    public const string TranscriptionDeployment = "gpt-4o-transcribe-cascade-menu-mode-conformance";
    public const string TtsDeployment = "gpt-4o-mini-tts-cascade-menu-mode-conformance";

    public const string BearerToken = "cascade-menu-mode-conformance-fake-bearer-token";

    public FakeChatCompletionsServer Chat { get; } = new();

    protected override string? Persona => Persona_;
    protected override IReadOnlyList<string>? Personas => [Persona_];
    protected override string? PersonasDir => RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());

    // Same reasoning as CascadeConformanceFixture's own Profile override -- see that class's doc
    // comment: the idle-timeout/nudge/first-frame knobs apply generically at the session-lifecycle
    // layer, not just to the realtime pipeline, so only the rate-limit retry delays need
    // shortening here and BackendProfiles.RateLimitTimers' own idle/grace/nudge budget
    // (10s/10s/10s) is further widened below via CONFORMANCE_IDLE_TIMEOUT_SECONDS etc., for the
    // identical reason that fixture's own doc comment documents.
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
                {"gpt-5-mini":"{{ChatDeployment}}","gpt-4o-transcribe":"{{TranscriptionDeployment}}","gpt-4o-mini-tts":"{{TtsDeployment}}"}
                """,
            ["CONFORMANCE_CASCADE_FAKE_TOKEN"] = BearerToken,
            ["CONFORMANCE_IDLE_TIMEOUT_SECONDS"] = "45",
            ["CONFORMANCE_GRACE_SECONDS"] = "45",
            ["CONFORMANCE_NUDGE_AFTER_SECONDS"] = "45",
        };
    }

    protected override async Task StopExtraFakesAsync() => await Chat.DisposeAsync().ConfigureAwait(false);

    // Rick's PR #253 review item 2 (follow-up): see CascadeConformanceFixture's matching
    // override for why this is wired through the base class's own post-body hook instead of a
    // hand-placed call at the top of every scenario in CascadeMenuModeAndVoiceConformanceTests.cs
    // (the 4 hand-placed calls that used to be there are removed -- this override supersedes
    // them for every scenario in this fixture's collection, present and future).
    protected override void AssertNoPendingExtraFakeState() => Chat.AssertNoPendingScriptedResponses();
}

[CollectionDefinition(Name)]
public sealed class CascadeMenuModeAndVoiceConformanceCollection : ICollectionFixture<CascadeMenuModeAndVoiceConformanceFixture>
{
    public const string Name = "ConformanceCascadeMenuModeAndVoice";
}

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
/// <see cref="ModelSelectionConformanceFixture"/>) -- `personas/sonic/persona.json` already
/// declares `models.cascade: {default: gpt-5-mini, allowed: [gpt-5-mini, phi-4]}` (#82's own
/// catalog entries), so no test-only fixture pack is needed to exercise cascade dispatch,
/// tool calling, or pricing against a real deployment-shaped persona.
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
        };
    }

    protected override async Task StopExtraFakesAsync() => await Chat.DisposeAsync().ConfigureAwait(false);
}

[CollectionDefinition(Name)]
public sealed class CascadeConformanceCollection : ICollectionFixture<CascadeConformanceFixture>
{
    public const string Name = "ConformanceCascade";
}

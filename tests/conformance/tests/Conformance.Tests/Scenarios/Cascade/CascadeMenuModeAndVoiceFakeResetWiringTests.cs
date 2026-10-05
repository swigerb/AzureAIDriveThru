using System.Text.Json.Nodes;
using Conformance.Fakes;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// Issue #274 follow-up B (#253 follow-up A): <see cref="CascadeFakeResetWiringTests"/> proves the
/// reset-on-throw wiring end to end for <see cref="CascadeConformanceFixture"/>'s own
/// <c>ResetExtraFakeState() =&gt; Chat.Drain()</c> override, but
/// <see cref="CascadeMenuModeAndVoiceConformanceFixture"/> is a SEPARATE, sealed fixture (see that
/// class's own doc comment for why: a dedicated `test-delta` persona pack rather than
/// <c>CascadeConformanceFixture</c>'s hard-coded real pack) with its OWN, separately-declared
/// <c>AssertNoPendingExtraFakeState</c>/<c>ResetExtraFakeState</c> overrides. Nothing before this
/// test exercised THIS fixture's own wiring -- deleting
/// <see cref="CascadeMenuModeAndVoiceConformanceFixture.ResetExtraFakeState"/>'s override (or its
/// <c>AssertNoPendingExtraFakeState</c> sibling) would leave every test in both
/// <see cref="CascadeMenuModeAndVoiceConformanceTests"/> and <see cref="FakeChatCompletionsServerTests"/>
/// green while this fixture's own real self-healing behaviour silently stopped working. Mirrors
/// <see cref="CascadeFakeResetWiringTests"/>'s exact scenario shape (arm a gate and leave an
/// unconsumed scripted response, throw before any request claims either, then prove an immediately
/// following ordinary scenario still completes cleanly instead of hanging) against THIS fixture's
/// own <see cref="CascadeMenuModeAndVoiceConformanceFixture.Chat"/> instance. Joins the shared
/// <see cref="CascadeMenuModeAndVoiceConformanceCollection"/> instead of starting a dedicated
/// fixture, same reasoning as <see cref="CascadeFakeResetWiringTests"/>'s own doc comment: this
/// costs nothing beyond the backend process every other test in that collection already shares.
/// </summary>
[Collection(CascadeMenuModeAndVoiceConformanceCollection.Name)]
public sealed class CascadeMenuModeAndVoiceFakeResetWiringTests(CascadeMenuModeAndVoiceConformanceFixture fixture)
{
    /// <summary>Same reasoning as <see cref="CascadeFakeResetWiringTests.DeliberateLeakScenarioException"/>'s
    /// own doc comment: a dedicated exception type so an assertion against it can only pass if the
    /// ORIGINAL marker survived, unmolested, as <see cref="ConformanceFixture.RunAsync(Func{Task})"/>'s
    /// wrapper's own <see cref="Exception.InnerException"/>.</summary>
    private sealed class DeliberateLeakScenarioException()
        : Exception("deliberate leak-scenario failure for CascadeMenuModeAndVoiceFakeResetWiringTests");

    [Fact]
    public async Task A_throwing_scenario_that_leaked_a_queued_response_and_an_armed_gate_does_not_hang_the_next_scenario()
    {
        var ct = TestContext.Current.CancellationToken;

        // Leak scenario: enqueues a response nobody will ever request, arms a gate nobody will
        // ever claim, then throws before issuing any request at all -- exactly the shape
        // CascadeFakeResetWiringTests' own doc comment describes.
        var marker = new DeliberateLeakScenarioException();
        var thrown = await Record.ExceptionAsync(() => fixture.RunAsync(() =>
        {
            fixture.Chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = "never requested" });
            fixture.Chat.HoldNextResponse();
            throw marker;
        }));

        // ConformanceFixture.RunAsync(Func<Task>) rewraps whatever the body throws as a NEW
        // InvalidOperationException with the original as InnerException whenever Backend is not
        // null (every fixture here always has a real Backend attached) -- asserting against the
        // wrapper's InnerException, not the wrapper itself, proves the original marker (and
        // therefore this specific scenario body) is what failed.
        var wrapped = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Same(marker, wrapped.InnerException);

        // Clean scenario, run immediately after: before this fixture's own Drain() wiring, this
        // scenario's own connect-time greeting request would be the very first /chat/completions
        // call FakeChatCompletionsServer.HandleCompletionAsync sees after the leak scenario above
        // -- it would inherit the still-armed gate from HoldNextResponse() and suspend on a
        // TaskCompletionSource nobody will ever release, until ConnectPastGreetingAsync's own
        // bounded FrameTimeout wait expires and IT throws instead, wrongly blaming this innocent
        // scenario for a leak the PREVIOUS scenario actually caused. If this scenario fails here,
        // either CascadeMenuModeAndVoiceConformanceFixture's own ResetExtraFakeState/
        // AssertNoPendingExtraFakeState overrides regressed, or the wiring that invokes them
        // (ConformanceFixture.RunAsync's own `finally` -> ResetExtraFakeState() -> Chat.Drain())
        // was removed or broken.
        await fixture.RunAsync(async () =>
        {
            var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(
                fixture, fixture.Chat, "gpt-5-mini", ct, persona: CascadeMenuModeAndVoiceConformanceFixture.Persona_);
            await connection.Browser.DisposeAsync();
        });
    }
}

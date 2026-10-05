using System.Text.Json.Nodes;
using Conformance.Fakes;
using Xunit;

namespace Conformance.Tests.Scenarios.Cascade;

/// <summary>
/// Rick's PR #253 re-review: the 3 tests in <see cref="FakeChatCompletionsServerTests"/> only
/// exercise <see cref="FakeChatCompletionsServer.Drain"/>/<see
/// cref="FakeChatCompletionsServer.AssertNoPendingScriptedResponses"/> directly against the bare
/// fake -- none of them prove the WIRING that actually invokes them in production,
/// <see cref="ConformanceFixture.RunAsync(Func{Task})"/>'s own <c>finally</c> calling the virtual
/// <see cref="ConformanceFixture.ResetExtraFakeState"/>, which <see
/// cref="CascadeConformanceFixture"/> overrides as <c>Chat.Drain()</c>. Deleting either that
/// override or the <c>finally</c> call site itself would leave every isolated harness test above
/// green while the real self-healing behaviour silently stopped working -- this test proves the
/// END-TO-END path instead: a scenario that arms <see
/// cref="FakeChatCompletionsServer.HoldNextResponse"/> and leaves an unconsumed scripted response,
/// then THROWS before ever sending the request that would have claimed either, followed
/// immediately by an ordinary scenario that must still complete cleanly. Joins the shared
/// <see cref="CascadeConformanceCollection"/> instead of starting a dedicated fixture, so this
/// costs nothing beyond the backend process every other Cascade test in this collection already
/// shares.
/// </summary>
[Collection(CascadeConformanceCollection.Name)]
public sealed class CascadeFakeResetWiringTests(CascadeConformanceFixture fixture)
{
    /// <summary>Distinguishes this test's own deliberate failure from any of
    /// <see cref="ConformanceFixture.RunAsync(Func{Task})"/>'s own exception types (its settle
    /// timeout and bound check both throw plain <see cref="InvalidOperationException"/>, the same
    /// type <see cref="ConformanceFixture.RunAsync(Func{Task})"/> itself wraps every caught
    /// exception in) -- so an assertion against this specific type can only pass if the ORIGINAL
    /// marker survived, unmolested, as the wrapper's <see cref="Exception.InnerException"/>.</summary>
    private sealed class DeliberateLeakScenarioException() : Exception("deliberate leak-scenario failure for CascadeFakeResetWiringTests");

    [Fact]
    public async Task A_throwing_scenario_that_leaked_a_queued_response_and_an_armed_gate_does_not_hang_the_next_scenario()
    {
        var ct = TestContext.Current.CancellationToken;

        // Leak scenario: enqueues a response nobody will ever request, arms a gate nobody will
        // ever claim (HoldNextResponse(), same call the barge-in row itself uses), then throws
        // before issuing any request at all -- exactly Rick's reported shape ("the scenario body
        // throws before its first request lands").
        var marker = new DeliberateLeakScenarioException();
        var thrown = await Record.ExceptionAsync(() => fixture.RunAsync(() =>
        {
            fixture.Chat.EnqueueMessage(new JsonObject { ["role"] = "assistant", ["content"] = "never requested" });
            fixture.Chat.HoldNextResponse();
            throw marker;
        }));

        // ConformanceFixture.RunAsync(Func<Task>) always has a real Backend attached for this
        // fixture, so its own `catch (Exception ex) when (Backend is not null)` rewraps whatever
        // the body throws as a NEW InvalidOperationException with the original as InnerException
        // (see that method's own doc comment) -- asserting against the wrapper's InnerException,
        // not the wrapper itself, is what actually proves the original marker (and therefore this
        // specific scenario body) is what failed, not some unrelated RunAsync-internal check.
        var wrapped = Assert.IsType<InvalidOperationException>(thrown);
        Assert.Same(marker, wrapped.InnerException);

        // Clean scenario, run immediately after: before Rick's fix, this scenario's own
        // connect-time greeting request would physically be the very first /chat/completions
        // call FakeChatCompletionsServer.HandleCompletionAsync sees after the leak scenario above
        // -- it would inherit the still-armed gate from HoldNextResponse() and suspend on a
        // TaskCompletionSource nobody will ever release, until ConnectPastGreetingAsync's own
        // bounded 30s FrameTimeout waits expire and IT throws instead, wrongly blaming this
        // innocent scenario for a leak the PREVIOUS scenario actually caused. If this scenario
        // fails here, either the Drain()/AssertNoPendingScriptedResponses() gate-release fix
        // itself regressed, or the wiring that invokes it (RunAsync's own `finally` ->
        // ResetExtraFakeState() -> Chat.Drain(), or CascadeConformanceFixture's own override of
        // either) was removed or broken.
        await fixture.RunAsync(async () =>
        {
            var connection = await CascadeScenarioHelpers.ConnectPastGreetingAsync(fixture, fixture.Chat, "gpt-5-mini", ct);
            await connection.Browser.DisposeAsync();
        });
    }
}

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #253 review (2nd follow-up): exercises
/// <see cref="FakeChatCompletionsServer.AssertNoPendingScriptedResponses"/> and
/// <see cref="FakeChatCompletionsServer.Drain"/> directly (no Python/C# backend involved),
/// mirroring <see cref="FakeRealtimeScriptingModelTests"/>'s own "test the fake in isolation"
/// pattern. These two members are the exact building blocks
/// <see cref="ConformanceFixture.AssertNoPendingExtraFakeState"/> and
/// <see cref="ConformanceFixture.ResetExtraFakeState"/> (overridden by both cascade fixtures to
/// call straight through to these) rely on, so proving their behaviour here proves the fix
/// end-to-end without needing to spin up a real backend process per test.
/// </summary>
[Trait("Category", "Harness")]
[Trait("Dotnet", "n/a-harness")] // Issue #21: backend-agnostic harness self-test, never exercises app/backend or app/backend-dotnet.
public sealed class FakeChatCompletionsServerTests
{
    private static JsonObject FinalMessage(string content) => new()
    {
        ["role"] = "assistant",
        ["content"] = content,
    };

    /// <summary>
    /// Before this fix, <see cref="FakeChatCompletionsServer.AssertNoPendingScriptedResponses"/>
    /// only read the queue's count -- it never cleared it -- so a single leaked scripted response
    /// failed every subsequent call to this method forever (each caller re-discovering and
    /// re-reporting the exact same stale entry). Scenario (a) from the PR review: "a leaking
    /// scenario fails once, and the next is clean."
    /// </summary>
    [Fact]
    public async Task AssertNoPendingScriptedResponses_clears_the_queue_before_throwing_so_a_later_call_is_clean()
    {
        await using var fake = new FakeChatCompletionsServer();
        fake.EnqueueMessage(FinalMessage("Never consumed by any request."));

        var ex = Assert.Throws<InvalidOperationException>(fake.AssertNoPendingScriptedResponses);
        Assert.Contains("1 scripted", ex.Message);

        // The queue is already empty by the time the exception above was thrown -- a second,
        // later call (standing in for the NEXT scenario sharing this same fake instance via
        // IClassFixture) must not re-discover the same leftover and must not throw at all.
        var exception = Record.Exception(fake.AssertNoPendingScriptedResponses);
        Assert.Null(exception);
    }

    /// <summary>
    /// A scenario body that throws before any request lands never reaches
    /// <see cref="ConformanceFixture.AssertNoPendingExtraFakeState"/> at all (that call sits AFTER
    /// the body in <c>ConformanceFixture.RunAsync</c>'s own try block) -- so without a reset on
    /// that failure path, a scripted response queued for a round that never happened would sit in
    /// the FIFO forever, waiting to be wrongly handed out to the next scenario's own first
    /// request. <c>ConformanceFixture.ResetExtraFakeState</c> (called from `RunAsync`'s `finally`
    /// only when the body didn't reach the post-body check) is wired, for both cascade fixtures,
    /// straight through to <see cref="FakeChatCompletionsServer.Drain"/> -- this test proves that
    /// call is sufficient to leave the fake clean for whatever runs next. Scenario (b) from the PR
    /// review: "a throwing scenario's leftover doesn't reach the next scenario."
    /// </summary>
    [Fact]
    public async Task Drain_clears_a_leak_left_behind_by_a_scenario_that_never_consumed_it()
    {
        await using var fake = new FakeChatCompletionsServer();
        fake.EnqueueMessage(FinalMessage("Scripted for a round whose request never arrived."));

        // Models ConformanceFixture.RunAsync's `finally` calling ResetExtraFakeState() (->
        // Chat.Drain() for both cascade fixtures) when the scenario body threw before ever
        // reaching its own AssertNoPendingExtraFakeState() check.
        fake.Drain();

        // The next scenario's own check (run, per the real fixture, immediately after ITS OWN
        // body returns) must see a clean queue -- the earlier scenario's throw must not have
        // leaked state into it.
        var exception = Record.Exception(fake.AssertNoPendingScriptedResponses);
        Assert.Null(exception);
    }

    /// <summary>A scenario with nothing left dangling must never fail this check.</summary>
    [Fact]
    public async Task AssertNoPendingScriptedResponses_does_not_throw_when_every_scripted_response_was_consumed()
    {
        await using var fake = new FakeChatCompletionsServer();

        var exception = Record.Exception(fake.AssertNoPendingScriptedResponses);

        Assert.Null(exception);
    }

    /// <summary>
    /// Issue #274 follow-up A (#253 re-review): the 3 tests above only ever exercise
    /// <see cref="FakeChatCompletionsServer.AssertNoPendingScriptedResponses"/>'s FIFO-leak branch
    /// (an unconsumed scripted response, with no armed gate) and <see cref="FakeChatCompletionsServer.Drain"/>'s
    /// own gate-release behaviour -- none of them call
    /// <see cref="FakeChatCompletionsServer.AssertNoPendingScriptedResponses"/> itself with an
    /// armed-but-empty gate (an <see cref="FakeChatCompletionsServer.HoldNextResponse"/> call with
    /// NO scripted response ever queued at all), the exact shape that exercises this method's own
    /// `leakedGate is not null` branch in isolation and selects its "An armed HoldNextResponse() gate
    /// was" message text (as opposed to the "N scripted ... response(s) were" or combined message --
    /// see that method's own `what` ternary). A mutation that flipped `leakedGate is not null` to
    /// always false, or that swapped the message strings, would survive every existing test here
    /// undetected.
    /// </summary>
    [Fact]
    public async Task AssertNoPendingScriptedResponses_with_only_an_armed_gate_throws_mentioning_the_gate_and_a_later_call_is_clean()
    {
        await using var fake = new FakeChatCompletionsServer();
        fake.HoldNextResponse();

        var ex = Assert.Throws<InvalidOperationException>(fake.AssertNoPendingScriptedResponses);
        Assert.Contains("An armed HoldNextResponse() gate was", ex.Message);

        // Mirrors the FIFO-leak test's own "a later call is clean" assertion above: the gate (like
        // the queue) is already cleared by the time the exception was thrown, so a second, later
        // call must not re-discover it and must not throw at all.
        var exception = Record.Exception(fake.AssertNoPendingScriptedResponses);
        Assert.Null(exception);
    }

    /// <summary>
    /// Rick's PR #253 re-review: <see cref="FakeChatCompletionsServer.HoldNextResponse"/> arms
    /// <c>_pendingResponseGate</c>, but only <see cref="FakeChatCompletionsServer.HandleCompletionAsync"/>
    /// (triggered by a REQUEST actually landing) used to ever clear it -- <see cref="FakeChatCompletionsServer.Drain"/>
    /// emptied the scripted-response queue but left an armed, never-claimed gate untouched. A
    /// scenario that calls <see cref="FakeChatCompletionsServer.HoldNextResponse"/> (e.g. to prove
    /// a turn is genuinely "in flight", like the barge-in row does) and then fails/throws BEFORE
    /// ever sending the request that would have claimed that gate leaves it armed in the field
    /// forever; the very next request to land here -- from ANY later scenario sharing this same
    /// fake, e.g. the next scenario's own connect-time greeting -- would otherwise wrongly inherit
    /// it and suspend on a <see cref="TaskCompletionSource"/> nobody will ever release, hanging
    /// until ITS OWN caller times out and blaming the wrong scenario. This proves <see
    /// cref="FakeChatCompletionsServer.Drain"/> now releases that gate too, so a real request sent
    /// AFTER <see cref="FakeChatCompletionsServer.Drain"/> completes promptly instead of hanging.
    /// </summary>
    [Fact]
    public async Task Drain_releases_an_armed_but_never_claimed_HoldNextResponse_gate_so_the_next_request_does_not_hang()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = new FakeChatCompletionsServer();
        await fake.StartAsync(ct);

        // Models a scenario that armed the gate (e.g. to later prove a turn is suspended on it)
        // but never sent the request that would have claimed it -- HandleCompletionAsync only
        // clears _pendingResponseGate when a request arrives, so without Drain() also releasing
        // it, this field would stay armed indefinitely.
        fake.HoldNextResponse();

        // Models ConformanceFixture.RunAsync's `finally` calling ResetExtraFakeState() ->
        // Chat.Drain() for both cascade fixtures, when the scenario that armed the gate above
        // threw before reaching its own AssertNoPendingExtraFakeState() check.
        fake.Drain();

        // The next scenario's own first request (modelled here as a bare POST, same contract a
        // real cascade client uses) must complete promptly -- before this fix, it would have
        // inherited the stale armed gate and suspended until this HttpClient's own timeout, with
        // nothing ever logged to explain why.
        using var client = new HttpClient { BaseAddress = fake.BaseUri, Timeout = TimeSpan.FromSeconds(10) };
        using var content = new StringContent(
            """{"messages":[],"model":"whatever"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/chat/completions?api-version=2024-05-01-preview", content, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

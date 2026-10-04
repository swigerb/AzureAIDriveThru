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
}

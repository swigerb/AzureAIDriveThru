using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Models;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Time.Testing;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #252 (Rick's review of #237's CI flake -- same root cause independently diagnosed and
/// fixed in this PR before this feedback arrived): a deterministic, end-to-end test of
/// <see cref="RealtimeProcessor.ForwardClientFrameAsync"/> -- the REAL production method
/// <c>RelayBrowserToUpstreamAsync</c> calls for every non-fast-path client-&gt;server frame, not a
/// parallel re-implementation -- proving the exact ordering property the fix relies on: recording
/// "this response.create was browser-initiated" before forwarding it upstream means a pending
/// retry survives even when the upstream's reply to that SAME frame is fully processed before the
/// send call returns (the precise interleaving a real race under scheduling pressure would
/// produce, reproduced here with zero threads/timing via <see cref="FakeWebSocket.OnSendAsync"/>).
/// </summary>
public sealed class ForwardClientFrameOrderingTests
{
    private static RealtimeProcessor CreateProcessor(TimeProvider? timeProvider = null) =>
        new(
            ModelCatalog.FromConfig(AppConfig.Load()),
            defaultDeployment: "gpt-realtime-2.1",
            upstreamEndpoint: "https://example-eastus2.openai.azure.com",
            upstreamApiKey: "sk-not-used",
            sessionConfig: new RealtimeSessionConfig(),
            promptLoaders: new Dictionary<string, PromptLoader>(),
            toolExecutor: new StubToolExecutor([]),
            timeProvider: timeProvider);

    private static JsonObject ResponseCreateFrame() => new() { ["type"] = "response.create" };

    private static JsonObject RateLimitedErrorEvent() =>
        new()
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["code"] = "rate_limit_exceeded", ["message"] = "Please try again in 1.5 seconds." },
        };

    [Fact]
    public async Task ForwardClientFrameAsync_PreservesRaceWonRetry_WhenUpstreamRepliesBeforeSendReturns()
    {
        var time = new FakeTimeProvider();
        var processor = CreateProcessor(time);
        var echo = new EchoSuppressor(cooldownSeconds: 2.0, flushSendAsync: _ => Task.CompletedTask, timeProvider: time);
        var upstreamSocket = new FakeWebSocket([]);
        var rateLimit = new RateLimitRecovery(
            new RateLimitSettings(),
            sendUpstream: (payload, ct) => upstreamSocket.SendAsync(
                System.Text.Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, endOfMessage: true, ct),
            sendClient: (_, _) => Task.CompletedTask,
            timeProvider: time);

        // The crux of the race: while the browser's response.create is still being sent upstream
        // (the real SendAsync call has not yet returned to its caller), the upstream's own reply to
        // THIS EXACT frame is fully processed -- here, synchronously, inside the fake socket's send
        // hook -- scheduling the ladder's first retry. A real race could land this interleaving
        // under scheduling pressure; this test forces it on every run, deterministically. The hook
        // disarms itself after firing once: only the ORIGINAL browser frame is racing here -- the
        // retry's own later response.create send must behave completely normally (it has no reply
        // raced against it in this test), not re-trigger the same simulated race recursively.
        var raceArmed = true;
        upstreamSocket.OnSendAsync = async bytes =>
        {
            if (!raceArmed)
            {
                return;
            }
            raceArmed = false;
            var sent = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.Contains("response.create", sent, StringComparison.Ordinal);
            var scheduled = await rateLimit.OnErrorAsync(RateLimitedErrorEvent(), CancellationToken.None);
            Assert.True(scheduled, "the fake upstream reply must itself be recognised as the rate-limit failure that schedules a retry");
            Assert.True(rateLimit.Busy, "OnErrorAsync must have scheduled a pending retry for this test to be meaningful");
        };

        await processor.ForwardClientFrameAsync(
            ResponseCreateFrame(), "response.create", echo, rateLimit, upstreamSocket, CancellationToken.None);

        // The fix: OnExternalResponseCreate("browser") ran BEFORE the send, so by the time the
        // upstream's reply schedules the retry (inside the send hook above), there is nothing left
        // for OnExternalResponseCreate to have cancelled. The retry must still be pending.
        Assert.True(rateLimit.Busy, "issue #252 regression: the race-won retry must survive -- it must not be cancelled by the bookkeeping for the very frame that lost the race");
        Assert.Single(upstreamSocket.SentMessages); // only the original browser response.create so far -- the retry hasn't fired yet.

        // And it still actually fires on schedule, proving this isn't a stuck/zombie state: a
        // second response.create (the retry itself) goes upstream once the delay elapses.
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, upstreamSocket.SentMessages.Count);
        Assert.True(rateLimit.Busy); // still true -- now awaiting the retry's own response.done, same as a non-raced retry would.
    }

    [Fact]
    public async Task ForwardClientFrameAsync_StillForwardsAndBargesInNormally_WhenNoRaceOccurs()
    {
        // Sanity/no-regression companion: the overwhelmingly common case (no race at all) must
        // still behave exactly as before the #252 reorder -- response.create is forwarded, and a
        // response.cancel still triggers barge-in echo suppression.
        var time = new FakeTimeProvider();
        var processor = CreateProcessor(time);
        var echo = new EchoSuppressor(cooldownSeconds: 2.0, flushSendAsync: _ => Task.CompletedTask, timeProvider: time);
        var upstreamSocket = new FakeWebSocket([]);
        var rateLimit = new RateLimitRecovery(
            new RateLimitSettings(),
            sendUpstream: (payload, ct) => upstreamSocket.SendAsync(
                System.Text.Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, endOfMessage: true, ct),
            sendClient: (_, _) => Task.CompletedTask,
            timeProvider: time);

        await processor.ForwardClientFrameAsync(
            ResponseCreateFrame(), "response.create", echo, rateLimit, upstreamSocket, CancellationToken.None);
        Assert.Single(upstreamSocket.SentMessages);
        Assert.False(rateLimit.Busy); // nothing failed -- no retry scheduled.

        await processor.ForwardClientFrameAsync(
            new JsonObject { ["type"] = "response.cancel" }, "response.cancel", echo, rateLimit, upstreamSocket, CancellationToken.None);
        Assert.Equal(2, upstreamSocket.SentMessages.Count);
    }
}

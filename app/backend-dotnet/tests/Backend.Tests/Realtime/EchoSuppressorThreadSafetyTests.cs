using Backend.Realtime;
using Microsoft.Extensions.Time.Testing;

namespace Backend.Tests.Realtime;

/// <summary>PR #140 R2: the browser and upstream relay loops call into one <see
/// cref="EchoSuppressor"/> instance truly in parallel on the thread pool (no single-threaded
/// asyncio loop to serialize them). These tests hammer the class from two real OS threads,
/// released together by a <see cref="Barrier"/> (no sleeps, so the two threads' iterations
/// actually interleave instead of running back-to-back), and assert nothing throws.
///
/// Round 2 (Rick's re-review, N3): these are no-throw smoke tests, not a mutation pin. With
/// <c>lock (_sync)</c> stripped from <see cref="EchoSuppressor"/>, both tests still pass 8 of 8
/// local runs, because its shared state is a compound bool/double update where nothing throws on
/// a torn read, and <see cref="EchoSuppressor.Close"/> only runs after both relay loops have
/// drained. The lock is accepted by inspection, not by mutation; only
/// <c>SessionUpdateGuardTests</c>' threading test is mutation-pinned (removing its lock fails 6 of
/// 8 runs).</summary>
public sealed class EchoSuppressorThreadSafetyTests
{
    [Fact]
    public void CooldownAcceptsFirstGuestAudio()
    {
        using var echo = new EchoSuppressor(cooldownSeconds: 1.5, flushSendAsync: static _ => Task.CompletedTask);
        echo.OnAudioDelta();
        echo.OnAudioDone(10.0);

        Assert.False(echo.ShouldSuppressAudio(10.5));
        Assert.Equal(0.0, echo.CooldownEnd);
    }

    [Fact]
    public void CooldownGuestAudioCancelsDelayedFlush()
    {
        // Issue #13 Wave 2: converted from a real 150ms Task.Delay wait to a FakeTimeProvider
        // advance -- deterministic, no flakiness, and proves the delayed flush is genuinely wired
        // through the injected TimeProvider (EchoSuppressor's own Task.Delay(..., _timeProvider,
        // ...) call). flushSendAsync here returns an already-completed Task, so OnAudioDone's
        // *immediate* best-effort flush runs fully synchronously (no await ever yields) -- no race
        // between this assertion and that call.
        var fakeTime = new FakeTimeProvider();
        var flushes = 0;
        using var echo = new EchoSuppressor(
            cooldownSeconds: 0.05,
            flushSendAsync: _ =>
            {
                Interlocked.Increment(ref flushes);
                return Task.CompletedTask;
            },
            timeProvider: fakeTime);

        echo.OnAudioDelta();
        echo.OnAudioDone(10.0); // immediate flush (#1) fires synchronously; also arms a delayed one.
        Assert.False(echo.ShouldSuppressAudio(10.01)); // within cooldown -- cancels the delayed flush.

        fakeTime.Advance(TimeSpan.FromSeconds(1)); // long past the 0.05s cooldown on the fake clock.

        // Mutation check: if ShouldSuppressAudio stopped cancelling _flushCts, this would be 2 --
        // the cancelled delayed flush would fire once the fake clock crosses its due time.
        Assert.Equal(1, Volatile.Read(ref flushes));
    }

    [Fact]
    public void ConcurrentBargeInAndAudioEvents_NeverThrow()
    {
        using var echo = new EchoSuppressor(cooldownSeconds: 0.0, flushSendAsync: static _ => Task.CompletedTask);
        const int iterations = 10_000;
        using var barrier = new Barrier(2);
        Exception? browserException = null;
        Exception? upstreamException = null;

        // Mimics the browser relay loop: barge-in handling plus the audio-append suppression
        // check (RealtimeProcessor.cs's `state.Echo.ShouldSuppressAudio(...)` call).
        var browserThread = new Thread(() =>
        {
            barrier.SignalAndWait();
            try
            {
                for (var i = 0; i < iterations; i++)
                {
                    echo.OnBargeIn();
                    _ = echo.ShouldSuppressAudio(i);
                    echo.OnExternalResponseCreate();
                }
            }
            catch (Exception ex)
            {
                browserException = ex;
            }
        });

        // Mimics the upstream relay loop: audio deltas/completion and greeting bookkeeping.
        var upstreamThread = new Thread(() =>
        {
            barrier.SignalAndWait();
            try
            {
                for (var i = 0; i < iterations; i++)
                {
                    echo.StartGreetingSuppression();
                    echo.OnAudioDelta();
                    echo.OnAudioDone(i);
                    echo.OnSpeechStarted();
                    echo.OnResponseDone(i);
                }
            }
            catch (Exception ex)
            {
                upstreamException = ex;
            }
        });

        browserThread.Start();
        upstreamThread.Start();
        browserThread.Join();
        upstreamThread.Join();

        Assert.Null(browserException);
        Assert.Null(upstreamException);
    }

    [Fact]
    public void ConcurrentCloseDuringAudioDone_NeverThrows()
    {
        // R2's F1 invariant (Close() is terminal) must hold even when Close() races the exact
        // window where OnAudioDone() is replacing _flushCts.
        using var barrier = new Barrier(2);
        Exception? audioException = null;
        Exception? closeException = null;

        for (var trial = 0; trial < 200; trial++)
        {
            var echo = new EchoSuppressor(cooldownSeconds: 0.01, flushSendAsync: static _ => Task.CompletedTask);
            var audioThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    for (var i = 0; i < 50; i++)
                    {
                        echo.OnAudioDelta();
                        echo.OnAudioDone(i);
                    }
                }
                catch (Exception ex)
                {
                    audioException = ex;
                }
            });
            var closeThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    echo.Close();
                }
                catch (Exception ex)
                {
                    closeException = ex;
                }
            });

            audioThread.Start();
            closeThread.Start();
            audioThread.Join();
            closeThread.Join();
            echo.Dispose();
        }

        Assert.Null(audioException);
        Assert.Null(closeException);
    }

    // ── Rick's #235 review: audited EchoSuppressor for RateLimitRecovery's stale-timer-
    // continuation pattern and found the identical shape in OnAudioDone's delayed flush. Fixed
    // the same way (FlushIfStillPendingAsync takes the scheduling CTS as its identity and only
    // proceeds if it is still the live _flushCts) -- this test proves it with the same
    // hold-the-lock-while-advancing-the-fake-clock technique RateLimitRecoveryUnitTests uses.
    [Fact]
    public async Task StaleFlushContinuation_LosesRaceToClose_NeverSends()
    {
        var fakeTime = new FakeTimeProvider();
        var flushes = 0;
        using var echo = new EchoSuppressor(
            cooldownSeconds: 1.5,
            flushSendAsync: _ =>
            {
                Interlocked.Increment(ref flushes);
                return Task.CompletedTask;
            },
            timeProvider: fakeTime);

        echo.OnAudioDelta();
        echo.OnAudioDone(10.0); // arms the delayed flush; also fires an immediate flush (#1).
        Assert.Equal(1, Volatile.Read(ref flushes));

        var syncRoot = echo.SyncRootForTests;
        Task advanceTask;
        Monitor.Enter(syncRoot);
        try
        {
            advanceTask = Task.Run(() => fakeTime.Advance(TimeSpan.FromSeconds(1.5)), TestContext.Current.CancellationToken);
            // Same scheduling-bias technique as RateLimitRecoveryUnitTests.RunStaleRetryRaceAsync:
            // give the background thread time to reach (and block on) _sync before this thread
            // wins the race by calling Close() first.
            Thread.Sleep(100);
            echo.Close();
        }
        finally
        {
            Monitor.Exit(syncRoot);
        }

        var finished = await Task.WhenAny(advanceTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) == advanceTask;
        Assert.True(finished, "the stale flush continuation deadlocked instead of returning once superseded");
        await advanceTask; // surface any exception thrown on the background thread.

        // Mutation check (manual, see PR description): without the CTS identity check in
        // FlushIfStillPendingAsync, this would be 2 -- the stale continuation (already past its
        // outer `!t.IsCanceled` check before Close() ever ran) would still fire the delayed flush
        // after Close() had already made the suppressor terminal.
        Assert.Equal(1, Volatile.Read(ref flushes));
    }
}

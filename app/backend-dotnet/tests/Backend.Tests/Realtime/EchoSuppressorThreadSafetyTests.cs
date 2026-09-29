using Backend.Realtime;

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
}

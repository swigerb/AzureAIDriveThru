using System.Diagnostics;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Deterministic, mutation-checked unit tests for <see cref="CapturedProcessOutput"/>'s two
/// event-driven wait primitives (PR #66 M2) -- <see cref="CapturedProcessOutput.WaitForDiagnosticsAsync"/>
/// (#55) and <see cref="CapturedProcessOutput.WaitForOutputQuiescenceAsync"/> (#62), plus S1-S4's
/// changes to each. Drives <see cref="CapturedProcessOutput.Append"/> directly (it is <c>internal</c>
/// precisely so these tests can do this -- see that member's own doc comment) instead of racing a
/// real child process's own ThreadPool callback timing, which is exactly the nondeterminism these
/// methods exist to protect production callers from in the first place. <see
/// cref="CapturedProcessOutputTests"/> covers <see cref="CapturedProcessOutput.CountUnhandledErrors"/>
/// against a real process; <see cref="Fixture_level_M1_late_error_from_scenario_A_is_attributed_to_A"/>
/// below covers the M1 fixture-level scenario-boundary case, which for realism needs a real process
/// on the real async stderr-capture path (this file's other tests do not).
/// </summary>
[Trait("Category", "Harness")]
[Trait("Dotnet", "n/a-harness")] // Issue #21: backend-agnostic harness self-test, never exercises app/backend or app/backend-dotnet.
public sealed class CapturedProcessOutputWaitTests
{
    // Generous enough to be reliably distinguishable from CI scheduling jitter without making
    // this file slow -- the same order of magnitude as this fix's own BaselineQuiescenceWindow
    // (250ms) constant in ConformanceFixture.cs.
    private static readonly TimeSpan s_shortIdleWindow = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan s_shortMaxWait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task WaitForDiagnosticsAsync_returns_true_immediately_when_the_predicate_already_matches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();
        output.Append("ERR", "already here before anyone waits");

        var sw = Stopwatch.StartNew();
        bool matched = await output.WaitForDiagnosticsAsync(
            d => d.Contains("already here", StringComparison.Ordinal), TimeSpan.FromSeconds(5), ct);
        sw.Stop();

        Assert.True(matched);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"Expected an already-true predicate to return near-instantly, took {sw.Elapsed}.");
    }

    /// <summary>
    /// Mutation check for #55 itself: if <c>WaitForDiagnosticsAsync</c> were made instantaneous
    /// (a single <c>predicate(DumpSince(...))</c> read with no event-driven wait at all -- exactly
    /// the pre-#55 shape this fix replaced), this goes red, because the matching line is appended
    /// on a delay, after the instantaneous read would already have missed it.
    /// </summary>
    [Fact]
    public async Task WaitForDiagnosticsAsync_returns_true_once_a_matching_line_is_appended_later()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();
        _ = Task.Run(async () =>
        {
            await Task.Delay(200, ct);
            output.Append("ERR", "the line arrives late");
        }, ct);

        bool matched = await output.WaitForDiagnosticsAsync(
            d => d.Contains("arrives late", StringComparison.Ordinal), TimeSpan.FromSeconds(5), ct);

        Assert.True(matched);
    }

    [Fact]
    public async Task WaitForDiagnosticsAsync_returns_false_on_timeout_when_the_predicate_never_matches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();
        output.Append("ERR", "never contains the target phrase");

        bool matched = await output.WaitForDiagnosticsAsync(
            d => d.Contains("no such phrase", StringComparison.Ordinal), TimeSpan.FromMilliseconds(300), ct);

        Assert.False(matched);
    }

    /// <summary>Mutation check for #66 S3: if a cancelled wait fell through to a final predicate
    /// check instead of propagating (the pre-S3 shape, where a per-wake Task.Delay reallocation
    /// also lost this), this goes red with a false/true return instead of the expected throw.</summary>
    [Fact]
    public async Task WaitForDiagnosticsAsync_propagates_a_genuine_cancellation_instead_of_treating_it_as_a_timeout()
    {
        var output = new CapturedProcessOutput();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            output.WaitForDiagnosticsAsync(
                _ => false, TimeSpan.FromSeconds(30), cts.Token));
    }

    /// <summary>
    /// Mutation check for #66 S2: if <c>sinceWatermark</c> were ignored (predicate checked against
    /// the whole unscoped <see cref="CapturedProcessOutput.Dump"/> history, the pre-S2 shape),
    /// this goes red -- the predicate would already match the earlier, out-of-scope line and
    /// return <c>true</c> instantly without ever waiting for the truly-in-scope one, which this
    /// test never appends at all.
    /// </summary>
    [Fact]
    public async Task WaitForDiagnosticsAsync_sinceWatermark_ignores_a_matching_line_captured_before_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();
        output.Append("ERR", "an earlier scenario's own fallback-timeout warning");
        int watermark = output.Watermark;

        bool matched = await output.WaitForDiagnosticsAsync(
            d => d.Contains("fallback-timeout warning", StringComparison.Ordinal),
            TimeSpan.FromMilliseconds(300),
            ct,
            sinceWatermark: watermark);

        Assert.False(matched);
    }

    [Fact]
    public async Task WaitForDiagnosticsAsync_sinceWatermark_still_matches_a_line_captured_after_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();
        output.Append("ERR", "an earlier scenario's own fallback-timeout warning");
        int watermark = output.Watermark;
        output.Append("ERR", "this scenario's own fallback-timeout warning");

        bool matched = await output.WaitForDiagnosticsAsync(
            d => d.Contains("this scenario's own fallback-timeout warning", StringComparison.Ordinal),
            TimeSpan.FromMilliseconds(300),
            ct,
            sinceWatermark: watermark);

        Assert.True(matched);
    }

    [Fact]
    public async Task WaitForOutputQuiescenceAsync_returns_true_when_nothing_was_ever_appended()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();

        Assert.True(await output.WaitForOutputQuiescenceAsync(s_shortIdleWindow, s_shortMaxWait, ct));
    }

    /// <summary>
    /// Mutation check for #62 itself: if <c>WaitForOutputQuiescenceAsync</c> were made a no-op
    /// (returns immediately without ever re-checking, the pre-#62 shape this fix replaced), this
    /// goes red, because it would return before the second, still-in-flight line lands -- the
    /// caller's baseline/actual read taken right after would then race that line exactly as #62
    /// describes.
    /// </summary>
    [Fact]
    public async Task WaitForOutputQuiescenceAsync_waits_out_a_still_arriving_second_line_before_returning()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();
        output.Append("ERR", "first line");
        _ = Task.Run(async () =>
        {
            await Task.Delay(80, ct);
            output.Append("ERR", "second, still-in-flight line");
        }, ct);

        var sw = Stopwatch.StartNew();
        bool quiesced = await output.WaitForOutputQuiescenceAsync(s_shortIdleWindow, s_shortMaxWait, ct);
        sw.Stop();

        Assert.True(quiesced);
        // Must have waited at least past the second line's own arrival plus its own idle window --
        // a no-op quiescence wait would have returned near-instantly, well under 80ms.
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(80),
            $"Expected this to wait out the still-arriving second line, only took {sw.Elapsed}.");
    }

    /// <summary>
    /// #103: this used to time-box its assertion with <c>sw.Elapsed &gt;= appendTimes[^1] +
    /// ShortIdleWindow - 30ms</c>, comparing a <see cref="Stopwatch"/> read (this test) against a
    /// separately-recomputed proxy for when the last line "really" landed. Those are two
    /// different clocks read at two different instants with an unsynchronized gap between them
    /// (this test's own <c>appendTimes.Add(sw.Elapsed)</c> runs a few instructions *after* <see
    /// cref="CapturedProcessOutput.Append"/> already captured <see
    /// cref="CapturedProcessOutput.LastAppendUtc"/> internally) -- ordinarily negligible, but a
    /// scheduler/GC pause landing in exactly that gap under CI load could stretch it past the
    /// 30ms margin, failing the assertion even though nothing was actually wrong. Comparing a
    /// fresh <c>DateTimeOffset.UtcNow</c> read against <see cref="CapturedProcessOutput.LastAppendUtc"/>
    /// itself (the *exact* timestamp the method under test armed its window from, not a proxy for
    /// it) removes that gap -- and the margin it needed -- entirely: real scheduling delay after
    /// the wait resumes can only make this test's own read later, never earlier, so the assertion
    /// can no longer flake in either direction.
    /// </summary>
    [Fact]
    public async Task WaitForOutputQuiescenceAsync_rearms_the_idle_window_on_every_new_line()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();
        // Seeds a recent _lastAppendUtc so the call below genuinely enters the debounce loop
        // instead of taking the #66 S1 fast path on its very first check (an entirely empty
        // history counts as "quiet forever" -- see that path's own doc comment -- which would
        // return true before ever observing any of the three lines appended below, making this
        // assert nothing about re-arming at all).
        output.Append("ERR", "seed line so the S1 fast path does not short-circuit this test");
        int appendCount = 0;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(100, ct);
                output.Append("ERR", $"line {i}");
                Interlocked.Increment(ref appendCount);
            }
        }, ct);

        bool quiesced = await output.WaitForOutputQuiescenceAsync(s_shortIdleWindow, s_shortMaxWait, ct);
        DateTimeOffset completedUtc = DateTimeOffset.UtcNow;

        Assert.True(quiesced);
        // Must not have returned before all three lines landed -- a stale, never-re-armed idle
        // window (the pre-#62 bug this test guards) would fire on just the first or second line
        // and return long before the background loop above ever finishes.
        Assert.Equal(3, Volatile.Read(ref appendCount));
        // Must not have returned before the LAST line's own full idle window elapsed, checked
        // against the exact instant that line was recorded internally (see this test's own doc
        // comment above for why that -- not an independent Stopwatch read -- is what removes the
        // flake without widening any margin).
        Assert.True(completedUtc - output.LastAppendUtc >= s_shortIdleWindow,
            $"Returned at {completedUtc:O}, but the last line was recorded at {output.LastAppendUtc:O} " +
            $"and needed a further {s_shortIdleWindow} of silence after that.");
    }

    /// <summary>Mutation check for #66 S4: if a cap hit still silently returned success (the
    /// pre-S4 <c>Task</c>-returning shape), this can't even compile against a caller that expects
    /// <c>Task&lt;bool&gt;</c> -- and would go red at runtime too, since a continuously-noisy
    /// backend would never satisfy a hard "true" expectation within the short cap used here.</summary>
    [Fact]
    public async Task WaitForOutputQuiescenceAsync_returns_false_when_maxWait_elapses_without_ever_going_quiet()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();
        // Same reasoning as the re-arm test above: seed a recent append first so this exercises
        // the real debounce-then-cap logic, not the S1 fast path's "nothing captured yet, so
        // trivially quiet" shortcut (which the churner below, started via Task.Run, might not
        // have produced its own first line for yet at the instant this call is made).
        output.Append("ERR", "seed line so the S1 fast path does not short-circuit this test");
        using var churnerStop = new CancellationTokenSource();
        var churner = Task.Run(async () =>
        {
            while (!churnerStop.IsCancellationRequested)
            {
                output.Append("ERR", "constant chatter, never idle");
                await Task.Delay(20, ct);
            }
        }, ct);

        bool quiesced = await output.WaitForOutputQuiescenceAsync(s_shortIdleWindow, TimeSpan.FromMilliseconds(300), ct);
        churnerStop.Cancel();
        await churner;

        Assert.False(quiesced);
    }

    /// <summary>Mutation check for #66 S3 on the quiescence side (mirrors the WaitForDiagnosticsAsync
    /// cancellation test above): a genuine cancellation must propagate, not silently resolve to
    /// true/false.</summary>
    [Fact]
    public async Task WaitForOutputQuiescenceAsync_propagates_a_genuine_cancellation()
    {
        var output = new CapturedProcessOutput();
        output.Append("ERR", "keeps the debounce armed"); // ensures this can't take the S1 fast path
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            output.WaitForOutputQuiescenceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), cts.Token));
    }

    /// <summary>
    /// Mutation check for #66 S1: if the fast path were removed (every call pays the full
    /// idle-window cost even when already silent, the pre-S1 shape), this goes red -- it asserts
    /// the already-silent case returns well under <see cref="s_shortIdleWindow"/>, which is only
    /// possible via the fast path.
    /// </summary>
    [Fact]
    public async Task WaitForOutputQuiescenceAsync_fast_path_returns_well_under_the_idle_window_when_already_silent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var output = new CapturedProcessOutput();
        output.Append("ERR", "one line, then genuinely nothing else ever again");
        await Task.Delay(s_shortIdleWindow + s_shortIdleWindow, ct); // already silent for 2 idle windows

        var sw = Stopwatch.StartNew();
        bool quiesced = await output.WaitForOutputQuiescenceAsync(s_shortIdleWindow, s_shortMaxWait, ct);
        sw.Stop();

        Assert.True(quiesced);
        Assert.True(sw.Elapsed < s_shortIdleWindow / 2,
            $"Expected the fast path to return in well under {s_shortIdleWindow} once already-silent, took {sw.Elapsed}.");
    }

    /// <summary>
    /// #66 M2's fixture-level M1 test: proves a late unhandled-backend-error line -- one that
    /// arrives after scenario A's own check has already run -- is attributed to scenario A by
    /// name, not silently folded into scenario B's baseline the way it would have been before
    /// #66 M1. Wires the real, production <see cref="CapturedProcessOutput"/> (attached to a real
    /// Python child process, on the real async <see cref="Process.ErrorDataReceived"/> ThreadPool
    /// dispatch path -- not a hand-driven in-memory fixture) together with the real
    /// <see cref="ScenarioErrorAttribution"/>, exactly the way <c>ConformanceFixture.RunAsync</c>
    /// wires them, deliberately bypassing <c>ConformanceFixture</c>/<c>Realtime</c>/<c>Search</c>
    /// itself (spinning up a full fixture with a real Python backend + fakes would be far slower
    /// and would reintroduce the exact kind of process-timing race this whole fix exists to
    /// eliminate from these tests). The child process is stdin-controlled -- it blocks on
    /// <c>input()</c> and, on receiving a "<c>&lt;delay_ms&gt;|&lt;message&gt;</c>" command line,
    /// sleeps exactly that long before writing <c>message</c> to stderr and looping back to
    /// <c>input()</c> -- so this test commands precisely-timed asynchronous stderr output with
    /// zero wall-clock racing, reproducing Rick's own PR #66 review repro (a child process that
    /// "writes an ERROR: line on cue") deterministically.
    /// Re-review update (R2): drives <see cref="ScenarioErrorAttribution.BeginScenario"/>/
    /// <see cref="ScenarioErrorAttribution.EndScenario"/> directly -- the same single-read entry
    /// points <c>ConformanceFixture.RunAsync</c> itself calls -- rather than hand-wiring the raw
    /// <see cref="ScenarioErrorAttribution.ChargeStrandedErrorsToPreviousScenario"/>/
    /// <see cref="ScenarioErrorAttribution.RecordScenarioChecked"/> pair, which is exactly how the
    /// fixture's own R1 bug (Assert.Fail running outside its try/finally) previously slipped past
    /// this test undetected.
    /// R4 fix (round-3 review): the previous version commanded scenario A's late error BEFORE its
    /// own post-body quiescence drain, then relied on scenario B's own drain having a fixed 500ms
    /// idle window (measured from an earlier line) that was still open when a 300ms-delayed error
    /// arrived -- about 200ms of margin for the Python sleep, the pipe, and ThreadPool dispatch to
    /// fit inside. That margin is exactly the kind of wall-clock assumption #66 M1(b) exists to
    /// make safe, not to rely on: a loaded CI runner blew through it (CI failed at
    /// <c>Assert.NotNull() Failure: Value is null</c>; local repro confirmed 450ms still passes,
    /// 550ms reproduces the failure exactly). A drain's idle window is a heuristic that can miss an
    /// in-flight line by design -- a test that assumes it always catches one was testing the wrong
    /// contract. This version instead asserts the ATTRIBUTION CONTRACT once the error has provably
    /// been counted, with no drain and no wall-clock margin involved at all: scenario A's own
    /// <see cref="ScenarioErrorAttribution.EndScenario"/> check runs and passes strictly BEFORE the
    /// error is even written to the child process's stdin, so it structurally cannot observe it --
    /// not "probably won't", cannot. Then <see cref="CapturedProcessOutput.WaitForDiagnosticsAsync"/>
    /// polls the real, ever-increasing <see cref="CapturedProcessOutput.CountUnhandledErrors"/>
    /// value itself -- re-evaluated after every <see cref="CapturedProcessOutput.Append"/>, i.e.
    /// after <c>ScanLine</c> has actually incremented the count -- rather than a string match
    /// against the dumped text (<c>Append</c> enqueues a line before <c>ScanLine</c> increments the
    /// count, so a text predicate could match one cycle ahead of the count actually moving). The
    /// wait's own 30s cap is generous enough to swallow any of the lags below with room to spare,
    /// so the test only proceeds to scenario B's own <see cref="ScenarioErrorAttribution.BeginScenario"/>
    /// charge once the error has definitely landed -- however long the child process took to
    /// dispatch it. A <c>[Theory]</c> over 0ms (immediate), 550ms (the exact CI failure
    /// reproduction above) and 2000ms (a much longer lag) proves the fix is lag-independent, not
    /// merely no-longer-failing-at-one-specific-value; it still goes red if attribution itself is
    /// disabled (e.g. short-circuiting <see cref="ScenarioErrorAttribution.ChargeStrandedErrorsToPreviousScenario"/>
    /// to always return null) -- see the PR's mutation-check evidence.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(550)]
    [InlineData(2000)]
    public async Task Fixture_level_M1_late_error_from_scenario_A_is_attributed_to_A(int lagMs)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string Script =
            "import sys\n" +
            "while True:\n" +
            "    line = sys.stdin.readline()\n" +
            "    if not line:\n" +
            "        break\n" +
            "    line = line.rstrip('\\n')\n" +
            "    if not line:\n" +
            "        continue\n" +
            "    delay_ms, _, message = line.partition('|')\n" +
            "    import time\n" +
            "    time.sleep(int(delay_ms) / 1000.0)\n" +
            "    print(message, file=sys.stderr, flush=True)\n";

        string repoRoot = RepoPaths.FindRepoRoot();
        string pythonExe = RepoPaths.PythonExecutable(repoRoot);
        var startInfo = new ProcessStartInfo(pythonExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(Script);

        using var process = new Process { StartInfo = startInfo };
        var output = new CapturedProcessOutput();
        output.Attach(process);
        var attribution = new ScenarioErrorAttribution();

        Assert.True(process.Start(), "Failed to start the Python venv interpreter for this test.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.AutoFlush = true;

        try
        {
            // --- Scenario A ---
            (int baselineA, string? strandedBeforeA) = attribution.BeginScenario(output.CountUnhandledErrors);
            Assert.Null(strandedBeforeA); // nothing has run yet -- no history to charge against.

            // Scenario A's body: a harmless, immediate line -- realistic backend chatter, not
            // required by the assertions below, but keeps this test wired the same way
            // ConformanceFixture actually drives a scenario.
            await process.StandardInput.WriteLineAsync("0|INFO:sonic-drive-in:scenario A body ran");
            bool sawBodyOutput = await output.WaitForDiagnosticsAsync(
                d => d.Contains("scenario A body ran", StringComparison.Ordinal), TimeSpan.FromSeconds(5), ct);
            Assert.True(sawBodyOutput, "The immediate body-output line never arrived.");

            // Scenario A's own check runs and passes HERE -- strictly before the error below is
            // even written to the child process's stdin, so it structurally cannot observe it.
            // No drain, no idle window, no wall-clock margin: this is a guarantee, not a timing bet.
            (int actualA, bool withinBoundA) = attribution.EndScenario(
                "ScenarioA", output.CountUnhandledErrors, baselineA, allowedNewBackendErrors: 0);
            Assert.True(withinBoundA, "Scenario A's own check must pass -- its error has not been " +
                "written to the child process yet.");
            Assert.Equal(baselineA, actualA);

            // Now command the child to log the ERROR: line lagMs from now. The error is
            // guaranteed to be "late" relative to scenario A's own check above (already done and
            // passed), regardless of how large or small lagMs is.
            await process.StandardInput.WriteLineAsync(
                $"{lagMs}|ERROR:sonic-drive-in:late error from scenario A");

            // --- Scenario B ---
            // Wait on the real, monotonically-increasing unhandled-error COUNT itself -- not a
            // string match, and not a fixed-duration idle window -- until it has provably risen
            // past scenario A's own recorded count. This is re-evaluated every time a new line is
            // appended, i.e. after ScanLine has actually incremented the count, so it can never
            // observe a false match ahead of the real increment the way a text-based predicate
            // could (Append enqueues a line before ScanLine increments the count). 30s is a
            // generous cap, far larger than any lag exercised by this [Theory].
            bool errorLanded = await output.WaitForDiagnosticsAsync(
                _ => output.CountUnhandledErrors() > actualA, TimeSpan.FromSeconds(30), ct);
            Assert.True(errorLanded, "The late error from scenario A never landed within 30s.");

            (int _, string? strandedMessage) = attribution.BeginScenario(output.CountUnhandledErrors);

            Assert.NotNull(strandedMessage);
            Assert.Contains("ScenarioA", strandedMessage);
            Assert.DoesNotContain("ScenarioB", strandedMessage);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}

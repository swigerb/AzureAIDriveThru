using System.Net.WebSockets;
using System.Text;
using Backend.Configuration;
using Backend.Models;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tools;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #13 Wave 2: the <c>input_audio_buffer.append</c> fast path in
/// <see cref="RealtimeProcessor"/>'s browser→upstream relay loop. <c>addUserAudio()</c>
/// (useRealtime.tsx) sends exactly <c>{"type":"input_audio_buffer.append","audio":"BASE64"}</c>
/// roughly 10 times a second while the guest is talking; the fast path forwards that ONE exact
/// shape's original bytes straight to the upstream socket, skipping the JSON parse, allow-list
/// rebuild and re-serialize the slow path otherwise does for every frame. These tests exercise the
/// three extracted, directly-testable pieces (same idiom as
/// <see cref="RealtimeProcessorSessionBindingTests"/>'s use of the internal
/// <c>ResolveSessionBinding</c>) without needing a live upstream connection:
/// <see cref="RealtimeProcessor.TryMatchAppendFastPath"/> (shape match only),
/// <see cref="RealtimeProcessor.TryAppendFastPath"/> (shape match + echo-suppression gating),
/// <see cref="RealtimeProcessor.ForwardFastPathAudioAsync"/> (the actual
/// <c>if (!fastPath.Suppressed)</c> forward branch, end to end), and
/// <see cref="RealtimeProcessor.SendBytesAsync"/> (byte-identical forwarding).
/// </summary>
public sealed class AudioAppendFastPathTests
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

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // ── TryMatchAppendFastPath: shape matching ──────────────────────────────────────────────

    [Theory]
    [InlineData("""{"type":"input_audio_buffer.append","audio":"AAAA"}""")]
    [InlineData("""{"type":"input_audio_buffer.append","audio":""}""")]
    [InlineData("""{"type":"input_audio_buffer.append","audio":"AQIDBA=="}""")]
    [InlineData("""{"type":"input_audio_buffer.append","audio":"A+/9="}""")]
    public void TryMatchAppendFastPath_matches_the_exact_frontend_shape(string frame)
    {
        Assert.True(RealtimeProcessor.TryMatchAppendFastPath(Utf8(frame)));
    }

    [Theory]
    // An event_id, even a valid one, is a different shape -- the slow path still accepts it.
    [InlineData("""{"type":"input_audio_buffer.append","audio":"AAAA","event_id":"e1"}""")]
    // Key order swapped.
    [InlineData("""{"audio":"AAAA","type":"input_audio_buffer.append"}""")]
    // Any extra whitespace anywhere breaks the exact byte match.
    [InlineData(""" {"type":"input_audio_buffer.append","audio":"AAAA"}""")]
    [InlineData("""{"type": "input_audio_buffer.append","audio":"AAAA"}""")]
    [InlineData("""{"type":"input_audio_buffer.append","audio":"AAAA"} """)]
    // A byte outside the base64 alphabet in the audio value.
    [InlineData("""{"type":"input_audio_buffer.append","audio":"AA!A"}""")]
    // PR #49's own review history: an unanchored substring match could be spoofed by embedding
    // the fast-path marker string inside an unrelated/nested JSON value -- must not match.
    [InlineData("""{"type":"session.update","session":{"note":"{\"type\":\"input_audio_buffer.append\",\"audio\":\"AAAA\"}"}}""")]
    // A different, non-append type entirely.
    [InlineData("""{"type":"response.cancel"}""")]
    // Truncated mid-frame (a partial read/corrupt frame), missing the trailing "}".
    [InlineData("{\"type\":\"input_audio_buffer.append\",\"audio\":\"AAAA\"")]
    // Empty payload.
    [InlineData("")]
    public void TryMatchAppendFastPath_rejects_anything_other_than_the_exact_shape(string frame)
    {
        Assert.False(RealtimeProcessor.TryMatchAppendFastPath(Utf8(frame)));
    }

    [Fact]
    public void TryMatchAppendFastPath_allows_a_trailing_equals_sign_anywhere_matching_pythons_own_fast_path_looseness()
    {
        // rtmt.py's _CLIENT_APPEND_FAST_PATH_RE uses the character class [A-Za-z0-9+/=]*, which is
        // looser than both languages' own slow-path audio regex (^[A-Za-z0-9+/]*={0,2}$, '=' only
        // as 0-2 TRAILING padding characters). This is a deliberate, documented match of Python's
        // existing fast-path behaviour (issue #13's "match Python's handling exactly"), not a new
        // looseness introduced by the C# port -- see IsFastPathAudioAlphabetByte's doc comment.
        var frameWithMidStringEquals = Utf8("""{"type":"input_audio_buffer.append","audio":"A=A"}""");
        Assert.True(RealtimeProcessor.TryMatchAppendFastPath(frameWithMidStringEquals));
    }

    // ── TryAppendFastPath: shape match + echo-suppression gating ───────────────────────────

    [Fact]
    public void TryAppendFastPath_reports_NoMatch_for_a_non_fast_path_frame_regardless_of_echo_state()
    {
        var processor = CreateProcessor();
        using var echo = new EchoSuppressor(cooldownSeconds: 1.5, flushSendAsync: static _ => Task.CompletedTask);
        echo.OnAudioDelta(); // AI is "speaking" -- would suppress if this were a match.

        var result = processor.TryAppendFastPath(Utf8("""{"type":"response.cancel"}"""), echo);

        Assert.False(result.IsMatch);
        Assert.False(result.Suppressed);
    }

    [Fact]
    public void TryAppendFastPath_matches_and_is_not_suppressed_when_the_assistant_is_silent()
    {
        var processor = CreateProcessor();
        using var echo = new EchoSuppressor(cooldownSeconds: 1.5, flushSendAsync: static _ => Task.CompletedTask);

        var result = processor.TryAppendFastPath(
            Utf8("""{"type":"input_audio_buffer.append","audio":"AAAA"}"""), echo);

        Assert.True(result.IsMatch);
        Assert.False(result.Suppressed);
    }

    [Fact]
    public void TryAppendFastPath_matches_but_is_suppressed_while_the_assistant_is_speaking()
    {
        // Mutation check target: if the fast path forwarded unconditionally on a shape match
        // (skipping the ShouldSuppressAudio gate), this would start failing -- Suppressed must
        // stay true, and the caller in RealtimeProcessor.cs must not forward the frame.
        var processor = CreateProcessor();
        using var echo = new EchoSuppressor(cooldownSeconds: 1.5, flushSendAsync: static _ => Task.CompletedTask);
        echo.OnAudioDelta();

        var result = processor.TryAppendFastPath(
            Utf8("""{"type":"input_audio_buffer.append","audio":"AAAA"}"""), echo);

        Assert.True(result.IsMatch);
        Assert.True(result.Suppressed);
    }

    [Theory]
    // Rick's #229 review: the original version of this test only advanced the fake clock PAST
    // the 1.5s cooldown. A comparison bug that always took the "past cooldown" branch (e.g. a
    // hardcoded `false`, or a `<=` that always failed) would have passed that single case
    // vacuously. Covering both sides of the `loopTimeSeconds < _cooldownEnd` comparison -- advance
    // less than the cooldown (still-pending flush must be cancelled, CooldownEnd resets to 0) and
    // advance past it (nothing to cancel, CooldownEnd is untouched) -- means ANY wrong clock
    // reading fails at least one of the two cases below, not just a total system-clock swap.
    [InlineData(0.5, true)]  // within the 1.5s cooldown -- cancels the pending flush.
    [InlineData(3.0, false)] // past the 1.5s cooldown -- nothing left to cancel.
    public void TryAppendFastPath_computes_NowSeconds_from_the_injected_TimeProvider_not_the_system_clock(
        double advanceSeconds, bool expectCooldownCancelled)
    {
        // EchoSuppressor.ShouldSuppressAudio's own contract (see its doc comment): once the
        // assistant has finished speaking, it never suppresses guest audio again -- the cooldown
        // window only controls whether a late guest frame cancels the still-pending delayed
        // upstream-buffer-clear flush. This test proves NowSeconds() is genuinely sourced from the
        // injected TimeProvider (not a hidden system-clock fallback) by observing THAT side
        // effect: starting the FakeTimeProvider at its default year-2000 epoch makes its "now" an
        // enormous number of seconds compared to any real wall-clock reading, so if NowSeconds()
        // silently reverted to a system-clock source, a real wall-clock reading would always be
        // judged as "still within the cooldown" (tiny < huge) and CooldownEnd would incorrectly
        // reset to 0 regardless of how far the FAKE clock is actually advanced -- which the
        // advanceSeconds=3.0 case below catches directly.
        var fakeTime = new FakeTimeProvider();
        var processor = CreateProcessor(fakeTime);
        using var echo = new EchoSuppressor(cooldownSeconds: 1.5, flushSendAsync: static _ => Task.CompletedTask);
        var appendFrame = Utf8("""{"type":"input_audio_buffer.append","audio":"AAAA"}""");

        echo.OnAudioDelta();
        echo.OnAudioDone(Now(fakeTime));
        var cooldownEndAfterCompletion = echo.CooldownEnd;
        Assert.True(cooldownEndAfterCompletion > 0.0);

        fakeTime.Advance(TimeSpan.FromSeconds(advanceSeconds));

        var result = processor.TryAppendFastPath(appendFrame, echo);

        Assert.True(result.IsMatch);
        Assert.False(result.Suppressed); // never suppressed once the assistant has stopped speaking.
        Assert.Equal(expectCooldownCancelled ? 0.0 : cooldownEndAfterCompletion, echo.CooldownEnd);
    }

    private static double Now(TimeProvider timeProvider) => timeProvider.GetTimestamp() / (double)timeProvider.TimestampFrequency;

    // ── ForwardFastPathAudioAsync: RelayBrowserToUpstreamAsync's forward branch, end to end ──

    [Fact]
    public async Task ForwardFastPathAudioAsync_drives_the_full_not_suppressed_path_end_to_end()
    {
        // Rick's #229 review: TryAppendFastPath's own Suppressed field (tested above) and
        // SendBytesAsync's own byte-identical forwarding (tested below) are each proven in
        // isolation, but neither proves RelayBrowserToUpstreamAsync's actual
        // `if (!fastPath.Suppressed)` branch really wires "not suppressed" to "send these exact
        // bytes upstream". This drives the real shape match, the real (silent) echo-suppression
        // gate, and the real forward together, through a FakeWebSocket stand-in for upstream.
        var processor = CreateProcessor();
        using var echo = new EchoSuppressor(cooldownSeconds: 1.5, flushSendAsync: static _ => Task.CompletedTask);
        var upstream = new FakeWebSocket(Array.Empty<(byte[], bool, WebSocketMessageType)>());
        var payload = Utf8("""{"type":"input_audio_buffer.append","audio":"AQIDBA=="}""");

        var fastPath = processor.TryAppendFastPath(payload, echo); // assistant silent -- real gate says "forward".
        Assert.True(fastPath.IsMatch);
        Assert.False(fastPath.Suppressed);

        await processor.ForwardFastPathAudioAsync(fastPath, payload, upstream, sessionId: "s1", CancellationToken.None);

        var sent = Assert.Single(upstream.SentMessages);
        Assert.Equal(payload, sent.Data);
        Assert.Equal(WebSocketMessageType.Text, sent.MessageType);
        Assert.True(sent.EndOfMessage);
    }

    [Fact]
    public async Task ForwardFastPathAudioAsync_drives_the_full_suppressed_path_end_to_end_as_a_no_op()
    {
        // Mutation check target: if the forward branch stopped checking fastPath.Suppressed (or
        // the echo gate itself stopped suppressing while the assistant speaks), this frame would
        // reach upstream and the Assert.Empty below would fail.
        var processor = CreateProcessor();
        using var echo = new EchoSuppressor(cooldownSeconds: 1.5, flushSendAsync: static _ => Task.CompletedTask);
        echo.OnAudioDelta(); // assistant speaking -- real gate says "suppress".
        var upstream = new FakeWebSocket(Array.Empty<(byte[], bool, WebSocketMessageType)>());
        var payload = Utf8("""{"type":"input_audio_buffer.append","audio":"AQIDBA=="}""");

        var fastPath = processor.TryAppendFastPath(payload, echo);
        Assert.True(fastPath.IsMatch);
        Assert.True(fastPath.Suppressed);

        await processor.ForwardFastPathAudioAsync(fastPath, payload, upstream, sessionId: "s1", CancellationToken.None);

        Assert.Empty(upstream.SentMessages);
    }

    // ── SendBytesAsync: byte-identical forwarding ───────────────────────────────────────────

    [Fact]
    public async Task SendBytesAsync_forwards_the_exact_original_bytes_unchanged()
    {
        var socket = new FakeWebSocket(Array.Empty<(byte[], bool, WebSocketMessageType)>());
        var payload = Utf8("""{"type":"input_audio_buffer.append","audio":"AQIDBA=="}""");

        await RealtimeProcessor.SendBytesAsync(socket, payload, CancellationToken.None);

        var sent = Assert.Single(socket.SentMessages);
        Assert.Equal(payload, sent.Data);
        Assert.Equal(WebSocketMessageType.Text, sent.MessageType);
        Assert.True(sent.EndOfMessage);
    }

    [Fact]
    public async Task SendBytesAsync_is_a_no_op_once_the_socket_is_no_longer_open()
    {
        var socket = new FakeWebSocket(Array.Empty<(byte[], bool, WebSocketMessageType)>());
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        await RealtimeProcessor.SendBytesAsync(socket, Utf8("""{"type":"input_audio_buffer.append","audio":"AAAA"}"""), CancellationToken.None);

        Assert.Empty(socket.SentMessages);
    }
}

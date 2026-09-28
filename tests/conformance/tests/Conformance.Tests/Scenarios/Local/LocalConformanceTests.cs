using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Conformance.Tests.Scenarios.Cascade;
using Conformance.Tests.Scenarios.Ordering;
using Xunit;

namespace Conformance.Tests.Scenarios.Local;

/// <summary>A connected local-pipeline browser client past its own connect-time greeting, plus
/// the greeting's <c>response.done</c> sequence number to watermark later waits against (same
/// convention as <see cref="CascadeConnection"/>).</summary>
public sealed record LocalConnection(RealtimeBrowserClient Browser, int GreetingWatermark, JsonElement SessionMetadata);

public static class LocalScenarioHelpers
{
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The backend's own default persona id, from <c>GET /api/personas</c>.</summary>
    public static async Task<string> DefaultPersonaIdAsync(IBackendUnderTest backend, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var document = JsonDocument.Parse(await http.GetStringAsync(new Uri(backend.BaseUri, "/api/personas"), ct));
        return document.RootElement.GetProperty("default").GetString()!;
    }

    /// <summary>The selectable local model ids <c>GET /api/personas/{id}</c> offers for a pack
    /// (empty when the pack declares no local block at all).</summary>
    public static async Task<IReadOnlyList<string>> SelectableLocalModelsAsync(
        IBackendUnderTest backend, string personaId, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(backend.BaseUri, $"/api/personas/{personaId}"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.GetProperty("models").TryGetProperty("local", out var local))
        {
            return [];
        }
        return local.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray();
    }

    /// <summary>Connects bound to <paramref name="personaId"/>'s own local default model, waits
    /// for <c>extension.session_metadata</c>, then drains the connect-time greeting. The greeting's
    /// reply is scripted BEFORE connecting so its <c>/v1/chat</c> call can never consume a
    /// response a test scripts afterwards for its own guest turn.</summary>
    public static async Task<LocalConnection> ConnectPastGreetingAsync(
        LocalRuntimeConformanceFixture fixture, string personaId, CancellationToken ct)
    {
        var model = LocalPackData.LocalDefaultModel(fixture.PersonasDirectory, personaId);
        fixture.Runtime.EnqueueFinal("Welcome! What can I get for you?");
        var browser = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, persona: personaId, model: model, cancellationToken: ct).ConfigureAwait(false);
        try
        {
            var metadata = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Expected extension.session_metadata within {FrameTimeout}.");
            var greetingDone = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "response.done", FrameTimeout, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Expected the connect-time greeting's response.done within {FrameTimeout}.");
            return new LocalConnection(browser, greetingDone.Sequence, metadata.Json);
        }
        catch
        {
            await browser.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Issue #81 part 1 acceptance: the local pipeline keeps the same session metadata, tools,
/// structured results and client wire protocol as realtime/cascade, proven against
/// <see cref="FakeLocalRuntimeServer"/> (design doc section 7.6). Untagged: the C# backend has no
/// local processor yet. Every row is event-driven (frame waits, request-arrival signals, the held
/// chat gate); nothing sleeps.
/// </summary>
[Collection(LocalRuntimeConformanceCollection.Name)]
public sealed class LocalPipelineConformanceTests(LocalRuntimeConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = LocalScenarioHelpers.FrameTimeout;

    /// <summary>Bounds the final negative wait of the barge-in row, which runs only after the
    /// first turn's chat request is already proven aborted (same rationale as
    /// <c>CascadeConformanceTests</c>' own NegativeCheckTimeout).</summary>
    private static readonly TimeSpan NegativeCheckTimeout = TimeSpan.FromSeconds(1);

    [Theory]
    [MemberData(nameof(LocalPackData.PackIds), MemberType = typeof(LocalPackData))]
    public Task Local_dispatch_binds_session_metadata_to_the_pack_its_local_model_and_the_local_pipeline(string personaId) =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var model = LocalPackData.LocalDefaultModel(fixture.PersonasDirectory, personaId);
        var connection = await LocalScenarioHelpers.ConnectPastGreetingAsync(fixture, personaId, ct);
        await using var browser = connection.Browser;

        Assert.Equal(personaId, connection.SessionMetadata.GetProperty("persona").GetString());
        Assert.Equal(model, connection.SessionMetadata.GetProperty("model").GetString());
        Assert.Equal("local", connection.SessionMetadata.GetProperty("pipeline").GetString());
    });

    [Theory]
    [MemberData(nameof(LocalPackData.PackIds), MemberType = typeof(LocalPackData))]
    public Task Api_persona_detail_offers_the_local_model_once_a_runtime_is_configured(string personaId) =>
        fixture.RunAsync(async () =>
    {
        // Positive pairing for LocalRuntimeNotConfiguredTests' omission row: without this, "not
        // listed" could pass simply because the local block is never listed at all.
        var ct = TestContext.Current.CancellationToken;
        var model = LocalPackData.LocalDefaultModel(fixture.PersonasDirectory, personaId);
        Assert.Contains(model, await LocalScenarioHelpers.SelectableLocalModelsAsync(fixture.Backend!, personaId, ct));
    });

    [Fact]
    public Task Local_sends_a_greeting_automatically_on_connect() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var personaId = await LocalScenarioHelpers.DefaultPersonaIdAsync(fixture.Backend!, ct);
        var model = LocalPackData.LocalDefaultModel(fixture.PersonasDirectory, personaId);
        const string greeting = "Hi there, welcome in! What can I get started for you?";
        var chatWatermark = fixture.Runtime.ChatRequestCount;
        var speakWatermark = fixture.Runtime.SpeakRequests.Count;

        // No guest audio at all: the greeting fires off the connection alone.
        fixture.Runtime.EnqueueFinal(greeting);
        await using var browser = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, persona: personaId, model: model, cancellationToken: ct);

        var transcript = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(transcript is not null, "Expected the greeting's response.audio_transcript.delta.");
        Assert.Equal(greeting, transcript!.Json.GetProperty("delta").GetString());

        var audio = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "response.audio.delta", FrameTimeout, ct);
        Assert.True(audio is not null, "Expected at least one response.audio.delta for the spoken greeting.");
        var done = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "response.done", FrameTimeout, ct);
        Assert.True(done is not null, "Expected the greeting's response.done.");

        // Same chat + speech path as a guest turn: one /v1/chat carrying the system prompt first and
        // the greeting instruction as the latest user message, then /v1/speak with the reply text.
        var greetingChat = fixture.Runtime.ChatRequests[chatWatermark];
        var messages = greetingChat.RawBody.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[^1].GetProperty("role").GetString());
        Assert.True(greetingChat.RawBody.GetProperty("tools").GetArrayLength() > 0, "Tool definitions must reach /v1/chat.");

        var spoken = fixture.Runtime.SpeakRequests.Skip(speakWatermark).FirstOrDefault(s => s.Text == greeting);
        Assert.True(spoken is not null, "Expected /v1/speak to receive the greeting text.");
        Assert.False(string.IsNullOrWhiteSpace(spoken!.Voice), "Expected /v1/speak to carry a voice.");
    });

    [Fact]
    public Task Local_tool_calling_round_trip_reaches_get_order_and_returns_structured_json_to_the_client() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var personaId = await LocalScenarioHelpers.DefaultPersonaIdAsync(fixture.Backend!, ct);
        var connection = await LocalScenarioHelpers.ConnectPastGreetingAsync(fixture, personaId, ct);
        await using var browser = connection.Browser;
        var transcribeWatermark = fixture.Runtime.TranscribeBodyLengths.Count;

        fixture.Runtime.EnqueueToolCall("call_get_order_1", "get_order", "{}");
        fixture.Runtime.EnqueueFinal("Your order is currently empty.");
        fixture.Runtime.NextTranscript = "What's on my order so far?";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var userTranscript = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "conversation.item.input_audio_transcription.completed",
            FrameTimeout, ct);
        Assert.True(userTranscript is not null, "Expected the guest's transcription to reach the client.");
        Assert.Equal("What's on my order so far?", userTranscript!.Json.GetProperty("transcript").GetString());

        // Same wire event and shape rtmt.py and the cascade pipeline emit (design doc section 6).
        var toolResponse = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "extension.middle_tier_tool_response" &&
                 f.Json.TryGetProperty("tool_name", out var name) && name.GetString() == "get_order",
            FrameTimeout, ct);
        Assert.True(toolResponse is not null, $"Expected an extension.middle_tier_tool_response(get_order) within {FrameTimeout}.");
        Assert.Equal(0, OrderScenarioHelpers.GetOrderItemCount(toolResponse!.Json.GetProperty("tool_result").GetString()!));

        var finalAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(finalAnswer is not null, "Expected the final answer as response.audio_transcript.delta.");
        Assert.Equal("Your order is currently empty.", finalAnswer!.Json.GetProperty("delta").GetString());

        var roundTrip = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > finalAnswer.Sequence && f.Type == "extension.round_trip_token", FrameTimeout, ct);
        Assert.True(roundTrip is not null, "Expected extension.round_trip_token once the turn finished.");

        // The guest audio really went to /v1/transcribe (the local runtime, not an Azure fake) as
        // whole PCM16 samples.
        var transcribed = fixture.Runtime.TranscribeBodyLengths.Skip(transcribeWatermark).ToArray();
        Assert.Single(transcribed);
        Assert.True(transcribed[0] > 0 && transcribed[0] % 2 == 0, $"Expected a non-empty PCM16 body, got {transcribed[0]} bytes.");
    });

    [Fact]
    public Task Local_not_on_menu_rejection_matches_the_structured_shape_in_the_tool_message_fed_back_to_the_model() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var personaId = await LocalScenarioHelpers.DefaultPersonaIdAsync(fixture.Backend!, ct);
        var connection = await LocalScenarioHelpers.ConnectPastGreetingAsync(fixture, personaId, ct);
        await using var browser = connection.Browser;

        const string offMenuItem = "Dragonfruit Lava Soup";
        var requestWatermark = fixture.Runtime.ChatRequestCount;
        fixture.Runtime.EnqueueToolCall(
            "call_update_order_off_menu", "update_order",
            $$"""{"action":"add","item_name":"{{offMenuItem}}","size":"medium","quantity":1}""");
        fixture.Runtime.EnqueueFinal("Sorry, that's not on our menu. Anything else?");
        fixture.Runtime.NextTranscript = "Can I get a dragonfruit lava soup?";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var finalAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark && f.Type == "response.audio_transcript.delta", FrameTimeout, ct);
        Assert.True(finalAnswer is not null, "Expected the model's follow-up answer after the rejection.");

        // The rejection is TO_SERVER only: visible in the NEXT /v1/chat request's tool message.
        var followUp = fixture.Runtime.ChatRequests.Skip(requestWatermark).Skip(1).FirstOrDefault();
        Assert.True(followUp is not null, "Expected a second /v1/chat request carrying the tool result.");
        var toolMessage = followUp!.RawBody.GetProperty("messages").EnumerateArray()
            .LastOrDefault(m => m.TryGetProperty("role", out var role) && role.GetString() == "tool");
        Assert.True(toolMessage.ValueKind != JsonValueKind.Undefined, "Expected a tool-role message in the follow-up request.");
        Assert.Equal("call_update_order_off_menu", toolMessage.GetProperty("tool_call_id").GetString());
        OrderScenarioHelpers.AssertRejectionShape(
            toolMessage.GetProperty("content").GetString()!, expectedReason: "not_on_menu", expectedItemName: offMenuItem);

        var anyToolResponse = browser.ReceivedFrames.Snapshot().Any(f =>
            f.Sequence > connection.GreetingWatermark &&
            f.Type == "extension.middle_tier_tool_response" &&
            f.Json.TryGetProperty("tool_name", out var toolName) && toolName.GetString() == "update_order");
        Assert.False(anyToolResponse, "A not_on_menu rejection must never emit extension.middle_tier_tool_response.");
    });

    [Fact]
    public Task Local_barge_in_aborts_the_in_flight_chat_request_before_it_speaks() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var personaId = await LocalScenarioHelpers.DefaultPersonaIdAsync(fixture.Backend!, ct);
        var connection = await LocalScenarioHelpers.ConnectPastGreetingAsync(fixture, personaId, ct);
        await using var browser = connection.Browser;
        var requestWatermark = fixture.Runtime.ChatRequestCount;
        const string cancelledAnswer = "You should never hear this, the turn gets cancelled.";

        var gate = fixture.Runtime.HoldNextChatResponse();
        fixture.Runtime.EnqueueFinal(cancelledAnswer);
        fixture.Runtime.NextTranscript = "I'll get a medium drink.";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        // The first turn's /v1/chat genuinely landed and is suspended on the gate before barging in.
        Assert.True(await fixture.Runtime.WaitForChatRequestCountAsync(requestWatermark + 1, FrameTimeout, ct),
            "Expected the first turn's /v1/chat request to land.");

        fixture.Runtime.EnqueueFinal("Barge-in answer.");
        fixture.Runtime.NextTranscript = "Actually, never mind.";
        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);

        var secondAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "response.audio_transcript.delta" && f.Json.GetProperty("delta").GetString() == "Barge-in answer.",
            FrameTimeout, ct);
        Assert.True(secondAnswer is not null, "Expected the barge-in turn's own answer.");

        // Positive proof of cancellation: the backend dropped the held request itself.
        Assert.True(fixture.Runtime.ChatRequests[requestWatermark].Aborted,
            "Expected the first turn's /v1/chat request to be aborted by the backend, proving barge-in cancelled the in-flight turn.");

        gate.Release();
        var firstTurnAnswer = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > connection.GreetingWatermark &&
                 f.Type == "response.audio_transcript.delta" && f.Json.GetProperty("delta").GetString() == cancelledAnswer,
            NegativeCheckTimeout, ct);
        Assert.True(firstTurnAnswer is null, "A barged-in-on turn must never reach the client.");
        Assert.DoesNotContain(fixture.Runtime.SpeakRequests, s => s.Text == cancelledAnswer);
    });
}

/// <summary>
/// Issue #81 part 1: local mode is off unless <c>LOCAL_RUNTIME_ENDPOINT</c> is configured. Runs
/// on the default fixture, which never sets it (and <see cref="InheritedEnvironmentFilter"/>
/// strips any ambient value). Untagged, like every local row.
/// </summary>
[Collection(ConformanceCollection.Name)]
public sealed class LocalRuntimeNotConfiguredTests(ConformanceFixture fixture)
{
    [Theory]
    [MemberData(nameof(LocalPackData.PackIds), MemberType = typeof(LocalPackData))]
    public Task Local_model_is_rejected_with_404_before_the_websocket_opens_without_a_runtime(string personaId) =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var model = LocalPackData.LocalDefaultModel(fixture.PersonasDirectory, personaId);
        await ModelSelectionConformanceTestHelpers.AssertRealtimeConnectIs404Async(
            fixture.Backend!.BaseUri, $"persona={personaId}&model={model}", ct);
    });

    [Theory]
    [MemberData(nameof(LocalPackData.PackIds), MemberType = typeof(LocalPackData))]
    public Task Api_persona_detail_omits_the_local_model_without_a_runtime(string personaId) =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var model = LocalPackData.LocalDefaultModel(fixture.PersonasDirectory, personaId);
        Assert.DoesNotContain(model, await LocalScenarioHelpers.SelectableLocalModelsAsync(fixture.Backend!, personaId, ct));
    });
}

/// <summary>
/// Issue #81 part 1, item 4: an unreachable runtime must not leave the guest in silence. The
/// greeting (a chat failure) and a guest turn (a transcription failure) each send the pack's own
/// <c>generic_error</c> text as the assistant transcript, framed by response.created/response.done
/// so the frontend unmutes as usual. Each failure is logged once at ERROR, hence the allowance.
/// </summary>
[Collection(LocalUnreachableRuntimeConformanceCollection.Name)]
public sealed class LocalRuntimeUnreachableTests(LocalUnreachableRuntimeConformanceFixture fixture)
{
    private static readonly TimeSpan FrameTimeout = LocalScenarioHelpers.FrameTimeout;

    [Fact]
    public Task Unreachable_runtime_sends_the_packs_generic_error_notice_instead_of_silence() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var personaId = await LocalScenarioHelpers.DefaultPersonaIdAsync(fixture.Backend!, ct);
        var model = LocalPackData.LocalDefaultModel(fixture.PersonasDirectory, personaId);
        var notice = LocalPackData.GenericError(fixture.PersonasDirectory, personaId);

        await using var browser = await RealtimeBrowserClient.ConnectAsync(
            fixture.Backend!.BaseUri, persona: personaId, model: model, cancellationToken: ct);
        var metadata = await browser.ReceivedFrames.WaitForAsync(f => f.Type == "extension.session_metadata", FrameTimeout, ct);
        Assert.True(metadata is not null, "Expected extension.session_metadata: an unreachable runtime must not block the session.");

        var greetingNotice = await browser.ReceivedFrames.WaitForAsync(
            f => f.Type == "response.audio_transcript.delta" && f.Json.GetProperty("delta").GetString() == notice, FrameTimeout, ct);
        Assert.True(greetingNotice is not null, $"Expected the pack's generic_error ({notice}) when the greeting's chat call fails.");
        var greetingDone = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > greetingNotice!.Sequence && f.Type == "response.done", FrameTimeout, ct);
        Assert.True(greetingDone is not null, "Expected response.done after the notice.");

        await CascadeScenarioHelpers.SendGuestTurnAsync(browser, ct);
        var turnCreated = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > greetingDone!.Sequence && f.Type == "response.created", FrameTimeout, ct);
        Assert.True(turnCreated is not null, "Expected response.created for the failed guest turn's notice.");
        var turnNotice = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > turnCreated!.Sequence &&
                 f.Type == "response.audio_transcript.delta" && f.Json.GetProperty("delta").GetString() == notice,
            FrameTimeout, ct);
        Assert.True(turnNotice is not null, "Expected the pack's generic_error when the guest turn's transcription fails.");
        var turnDone = await browser.ReceivedFrames.WaitForAsync(
            f => f.Sequence > turnNotice!.Sequence && f.Type == "response.done", FrameTimeout, ct);
        Assert.True(turnDone is not null, "Expected response.done after the guest turn's notice.");

        Assert.DoesNotContain(browser.ReceivedFrames.Snapshot(), f => f.Type == "response.audio.delta");
    }, allowedNewBackendErrors: 2);
}

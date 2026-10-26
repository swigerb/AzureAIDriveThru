using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Models;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tests.Realtime;
using Backend.Tests.TestSupport;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

public sealed class CascadeTurnCoordinatorTests
{
    [Fact]
    public async Task BargeIn_cancelsImmediatelyButAnnouncesOnlyAfterThePreviousTurnFinishes()
    {
        var browser = new FakeWebSocket([]);
        using var connectionCts = new CancellationTokenSource();
        CascadeSessionContext context = NewContext(browser, connectionCts);
        var coordinator = new CascadeTurnCoordinator(context);
        using var turnCts = new CancellationTokenSource();
        var previousTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.State.CurrentTurnTask = previousTurn.Task;
        context.State.CurrentTurnCts = turnCts;

        coordinator.BargeIn();

        Assert.True(turnCts.IsCancellationRequested);
        Assert.Null(context.State.CurrentTurnTask);
        Assert.Empty(browser.SentMessages);
        previousTurn.SetResult();
        await context.State.BargeInTail!.WaitAsync(TestContext.Current.CancellationToken);

        JsonObject frame = JsonNode.Parse(Assert.Single(browser.SentMessages).Data)!.AsObject();
        Assert.Equal("input_audio_buffer.speech_started", (string?)frame["type"]);
        Assert.False(connectionCts.IsCancellationRequested);
    }

    [Fact]
    public async Task ConsecutiveBargeIns_keepTheEarlierTailAheadOfTheNextAnnouncement()
    {
        var browser = new FakeWebSocket([]);
        using var connectionCts = new CancellationTokenSource();
        CascadeSessionContext context = NewContext(browser, connectionCts);
        var coordinator = new CascadeTurnCoordinator(context);
        var previousTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.State.CurrentTurnTask = previousTurn.Task;

        coordinator.BargeIn();
        Task? firstTail = context.State.BargeInTail;
        coordinator.BargeIn();

        Assert.False(firstTail!.IsCompleted);
        Assert.Empty(browser.SentMessages);
        previousTurn.SetResult();
        await context.State.BargeInTail!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(firstTail.IsCompletedSuccessfully);
        Assert.Equal(2, browser.SentMessages.Count);
        Assert.All(browser.SentMessages, frame =>
            Assert.Equal("input_audio_buffer.speech_started", (string?)JsonNode.Parse(frame.Data)!["type"]));
    }

    private static CascadeSessionContext NewContext(FakeWebSocket browser, CancellationTokenSource connectionCts) => new()
    {
        BrowserSocket = browser,
        Persona = DeltaFixture.Load(),
        ResolvedModel = new ResolvedModel("gpt-5-mini", "cascade", "chat-dep", Reasoning: false),
        MenuMode = null,
        LinkedCts = connectionCts,
        CancellationToken = connectionCts.Token,
        SessionManager = null,
        State = new CascadeProcessor.CascadeSessionState { SessionId = "session-1", Deployment = "chat-dep" },
        PromptLoader = null,
        Detector = new TurnDetector(0.5, 500, 24000),
        AllowedVoices = new HashSet<string> { "marin" },
        Logger = NullLogger.Instance,
        TimeProvider = TimeProvider.System,
        TurnRegistryLock = new object(),
        SessionId = "session-1",
        ToolExecutor = new StubToolExecutor([]),
        Identifiers = new SessionIdentifiers("test-alpha", "gpt-5-mini", "session-1", pipeline: "cascade"),
    };
}

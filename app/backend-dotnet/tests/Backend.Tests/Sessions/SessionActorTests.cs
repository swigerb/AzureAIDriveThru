using System.Collections.Concurrent;
using Backend.Sessions;

namespace Backend.Tests.Sessions;

/// <summary>A no-op-friendly test processor that records the order events arrive in and, when
/// asked, sleeps briefly so a test can prove two events for the SAME session never overlap.</summary>
internal sealed class RecordingProcessor : IPipelineProcessor
{
    public ConcurrentQueue<int> ProcessedOrder { get; } = new();
    private int _concurrentCallCount;
    public int MaxObservedConcurrency { get; private set; }

    public string PipelineName => "test";

    /// <summary>Trivial stub -- this test double exercises SessionActor's mailbox-loop ordering
    /// only, never the persona/model dispatch seam (see Models/ModelDispatchTests.cs and
    /// Sessions/RealtimeProcessorTests.cs for that).</summary>
    public Backend.Models.ResolvedModel ResolveModel(Backend.Personas.Persona persona, string? requestedModelId) =>
        new(requestedModelId ?? "test-model", PipelineName, "test-deployment", Reasoning: false);

    public async Task ProcessAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref _concurrentCallCount);
        MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, current);
        await Task.Delay(10, cancellationToken);
        if (sessionEvent is NumberedEvent numbered)
        {
            ProcessedOrder.Enqueue(numbered.Number);
        }
        Interlocked.Decrement(ref _concurrentCallCount);
    }
}

internal sealed record NumberedEvent(int Number) : SessionEvent;

/// <summary>Proves issue #12's "one event loop per session" guarantee: events posted to a single
/// SessionActor are always processed strictly in order, one at a time, never concurrently.</summary>
public sealed class SessionActorTests
{
    [Fact]
    public async Task Events_ProcessedInPostOrder_NeverConcurrently()
    {
        var processor = new RecordingProcessor();
        var actor = new SessionActor("session-1", processor);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(actor.Post(new NumberedEvent(i)));
        }

        await actor.DisposeAsync();

        Assert.Equal(Enumerable.Range(0, 20), processor.ProcessedOrder);
        Assert.Equal(1, processor.MaxObservedConcurrency);
    }

    [Fact]
    public async Task NoProcessorBound_DrainsWithoutThrowing()
    {
        var actor = new SessionActor("session-no-processor");

        Assert.True(actor.Post(new NumberedEvent(1)));

        await actor.DisposeAsync();
    }

    [Fact]
    public async Task Post_AfterDispose_ReturnsFalse()
    {
        var actor = new SessionActor("session-2");

        await actor.DisposeAsync();

        Assert.False(actor.Post(new NumberedEvent(1)));
    }
}

public sealed class SessionRegistryTests
{
    [Fact]
    public void GetOrAdd_ReturnsSameActor_ForSameSessionId()
    {
        var registry = new SessionRegistry();

        var first = registry.GetOrAdd("s1", id => new SessionActor(id));
        var second = registry.GetOrAdd("s1", id => new SessionActor(id));

        Assert.Same(first, second);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task RemoveAsync_DisposesAndRemoves()
    {
        var registry = new SessionRegistry();
        registry.GetOrAdd("s1", id => new SessionActor(id));

        var removed = await registry.RemoveAsync("s1");

        Assert.True(removed);
        Assert.False(registry.TryGet("s1", out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task RemoveAsync_UnknownSessionId_ReturnsFalse()
    {
        var registry = new SessionRegistry();

        Assert.False(await registry.RemoveAsync("does-not-exist"));
    }
}

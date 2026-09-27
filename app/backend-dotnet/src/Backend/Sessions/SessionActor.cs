using System.Threading.Channels;

namespace Backend.Sessions;

/// <summary>
/// One sequential event loop per session (issue #12: "one event loop per session"), backed by an
/// unbounded <see cref="Channel{T}"/> mailbox instead of a lock -- events for a given session are
/// always processed strictly in arrival order, one at a time, with no risk of two events for the
/// same session ever running concurrently. <see cref="IPipelineProcessor"/> is not yet bound to
/// anything real (wave 7): posted events are drained in order but not acted on until a processor
/// is supplied.
/// </summary>
public sealed class SessionActor : IAsyncDisposable
{
    private readonly Channel<SessionEvent> _mailbox = Channel.CreateUnbounded<SessionEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly IPipelineProcessor? _processor;
    private readonly Task _loop;

    public SessionActor(string sessionId, IPipelineProcessor? processor = null, SessionMetadata? metadata = null)
    {
        SessionId = sessionId;
        _processor = processor;
        Metadata = metadata;
        _loop = Task.Run(RunLoopAsync);
    }

    public string SessionId { get; }

    /// <summary>Persona/model/pipeline this session bound to before its WebSocket upgraded (issue
    /// #12 part 2), or null for the still-supported no-processor case. See
    /// <see cref="SessionMetadata"/>.</summary>
    public SessionMetadata? Metadata { get; }

    /// <summary>Enqueues an event for this session. Returns false only if the actor has already
    /// been disposed (its mailbox is closed).</summary>
    public bool Post(SessionEvent sessionEvent) => _mailbox.Writer.TryWrite(sessionEvent);

    private async Task RunLoopAsync()
    {
        await foreach (var sessionEvent in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_processor is not null)
            {
                await _processor.ProcessAsync(sessionEvent, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Closes the mailbox (no further <see cref="Post"/> calls will succeed) and waits
    /// for the loop to drain whatever was already queued before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }
}

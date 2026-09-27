using System.Collections.Concurrent;

namespace Backend.Sessions;

/// <summary>Shared, process-wide registry of active <see cref="SessionActor"/> instances (issue
/// #12's "shared registry"). Thread-safe: concurrent registrations/lookups/removals are expected
/// from multiple concurrent /realtime upgrade requests and disconnect handlers.</summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionActor> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    public SessionActor GetOrAdd(string sessionId, Func<string, SessionActor> factory) =>
        _sessions.GetOrAdd(sessionId, factory);

    public bool TryGet(string sessionId, out SessionActor actor) => _sessions.TryGetValue(sessionId, out actor!);

    public async ValueTask<bool> RemoveAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var actor))
        {
            return false;
        }
        await actor.DisposeAsync().ConfigureAwait(false);
        return true;
    }
}

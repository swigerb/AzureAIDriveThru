using System.Collections.Concurrent;
using System.Text.Json;

namespace Backend.Search;

/// <summary>
/// Port of app/backend/tools.py's <c>_SearchCache</c>: a TTL + max-size cache of RAW Azure AI
/// Search records, namespaced by persona id (Python's module-level <c>_search_cache</c>, shared
/// by every session in the process -- a search result for a given persona+query never depends on
/// which session asked). Unlike Python's own explicitly "not thread-safe, but fine for
/// single-threaded asyncio" implementation, THIS cache genuinely can be hit concurrently (each
/// session is its own actor, and actors run independently) so it is backed by a <see
/// cref="ConcurrentDictionary{TKey, TValue}"/> and an atomic insertion counter rather than relying
/// on single-threaded access. Eviction policy mirrors Python's <c>put</c> exactly: when full,
/// evict whichever entry was inserted least recently (lowest insertion-order counter), then
/// insert the new one.
///
/// <para><b>#309 (R1):</b> this caches the RAW <c>List&lt;JsonElement&gt;</c> records Azure AI
/// Search returned, NOT a formatted/OOS-tagged <see cref="Tools.ToolResult"/> -- see the comment
/// at the cache lookup call site in <see cref="SearchTool.ExecuteAsync"/> for why. A cached entry
/// is safe to share across sessions/override states because <see cref="SearchTool"/> reformats it
/// (including the OOS tag) fresh on every call, cache hit or not.</para>
/// </summary>
internal sealed class SearchResultCache
{
    private sealed record Entry(List<JsonElement> Records, DateTime ExpiresAtUtc, long InsertedOrder);

    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private long _counter;

    public bool TryGet(string key, out List<JsonElement>? records)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow <= entry.ExpiresAtUtc)
            {
                records = entry.Records;
                return true;
            }
            // Expired -- best-effort remove (mirrors Python's `del self._store[key]` on a stale
            // read; a lost race with a concurrent writer refreshing the same key is harmless).
            _store.TryRemove(key, out _);
        }
        records = null;
        return false;
    }

    public void Put(string key, List<JsonElement> records, double ttlSeconds, int maxSize)
    {
        if (_store.Count >= maxSize && !_store.ContainsKey(key))
        {
            EvictOldest();
        }
        _store[key] = new Entry(records, DateTime.UtcNow.AddSeconds(ttlSeconds), Interlocked.Increment(ref _counter));
    }

    private void EvictOldest()
    {
        KeyValuePair<string, Entry>? oldest = null;
        foreach (var kv in _store)
        {
            if (oldest is null || kv.Value.InsertedOrder < oldest.Value.Value.InsertedOrder)
            {
                oldest = kv;
            }
        }
        if (oldest is { } found)
        {
            _store.TryRemove(found.Key, out _);
        }
    }
}

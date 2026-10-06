namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/rtmt.py's `_new_event_id` (issue #13's "event-id guard" acceptance
/// target). Every session.update this backend sends carries one, so a GA rejection can be
/// correlated back to the update that caused it (see <see cref="SessionUpdateGuard"/>).
/// </summary>
internal static class EventIds
{
    public static string NewEventId(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 1 + 20)];
}

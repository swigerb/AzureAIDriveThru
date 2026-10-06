namespace Backend.Tools;

/// <summary>
/// Issue #14, Rick's PR #149 R4 review: an optional, opt-in capability separate from
/// <see cref="IToolExecutor"/>'s own contract -- an <see cref="IToolExecutor"/> that also has a
/// current order ticket to refresh from. <see cref="Sessions.RealtimeProcessor"/> pattern-matches
/// (<c>is IOrderTicketSource</c>) after a genuine tool exception to best-effort push a fresh
/// <c>get_order</c>-shaped ticket to the browser, mirroring app/backend/rtmt.py's post-exception
/// <c>order_state_singleton.get_order_summary_json(session_id)</c> read.
///
/// <para>Deliberately kept off <see cref="IToolExecutor"/> itself: <c>StubToolExecutor</c> (the
/// no-op fallback used before persona binding resolves) has no order state to refresh from and
/// stays a valid <see cref="IToolExecutor"/> without implementing this -- the ticket-refresh
/// behavior is opt-in per executor, not a universal requirement.</para>
/// </summary>
public interface IOrderTicketSource
{
    /// <summary>The current order summary, serialized the same way <c>get_order</c>/
    /// <c>reset_order</c> already do (<see cref="OrderSummaryJson"/>). Reading this must not
    /// itself throw for any reachable order state -- callers still wrap the read in their own
    /// try/catch as defense in depth, matching rtmt.py's own best-effort read.</summary>
    string CurrentOrderSummaryJson { get; }
}

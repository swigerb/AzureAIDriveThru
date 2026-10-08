using System.Security.Cryptography;

namespace Backend.Realtime;

/// <summary>
/// Port of app/backend/session_manager.py's `new_middle_tier_item_id()`: every conversation item
/// the middle tier itself authors (the greeting, a tool's function_call_output) gets a fresh id
/// on every call, stamped with <see cref="ClientServerFilter.MiddleTierItemIdPrefix"/> so
/// <see cref="ClientServerFilter.DropFromClient"/> can recognise and drop it before it reaches the
/// browser -- and so GA never sees the same item id twice, which it rejects outright.
/// </summary>
internal static class MiddleTierItemIds
{
    public static string NewId() =>
        ClientServerFilter.MiddleTierItemIdPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
}

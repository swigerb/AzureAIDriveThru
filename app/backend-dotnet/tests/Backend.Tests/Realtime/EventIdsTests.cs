using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>Byte-for-byte port test for rtmt.py's `_new_event_id`.</summary>
public sealed class EventIdsTests
{
    [Fact]
    public void NewEventId_HasPrefixUnderscoreAndTwentyHexChars()
    {
        var id = EventIds.NewEventId("sonic_bootstrap");

        Assert.StartsWith("sonic_bootstrap_", id);
        var suffix = id["sonic_bootstrap_".Length..];
        Assert.Equal(20, suffix.Length);
        Assert.Matches("^[0-9a-f]{20}$", suffix);
    }

    [Fact]
    public void NewEventId_IsUniqueAcrossCalls()
    {
        var a = EventIds.NewEventId("sonic_su");
        var b = EventIds.NewEventId("sonic_su");

        Assert.NotEqual(a, b);
    }
}

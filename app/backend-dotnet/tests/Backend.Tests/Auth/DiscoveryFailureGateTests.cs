using Backend.Auth;

namespace Backend.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="DiscoveryFailureGate"/> (#223 item 1, Rick's review of PR #225,
/// mirrored here for #147 parity) -- entra_auth.py's planned 30s negative-discovery-cache
/// cooldown. A deterministic <see cref="FakeTimeProvider"/> stands in for wall-clock time so the
/// cooldown boundary can be asserted exactly, without a real Task.Delay/sleep.
/// </summary>
public sealed class DiscoveryFailureGateTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void IsInCooldown_FalseInitially_NoFailureRecordedYet()
    {
        var gate = new DiscoveryFailureGate();

        Assert.False(gate.IsInCooldown());
    }

    [Fact]
    public void IsInCooldown_TrueImmediatelyAfterRecordFailure()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new DiscoveryFailureGate(clock, TimeSpan.FromSeconds(30));

        gate.RecordFailure();

        Assert.True(gate.IsInCooldown());
    }

    [Fact]
    public void IsInCooldown_StaysTrueJustBeforeCooldownExpires()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new DiscoveryFailureGate(clock, TimeSpan.FromSeconds(30));

        gate.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(29.9));

        Assert.True(gate.IsInCooldown());
    }

    [Fact]
    public void IsInCooldown_FalseOnceCooldownWindowElapses()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new DiscoveryFailureGate(clock, TimeSpan.FromSeconds(30));

        gate.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.False(gate.IsInCooldown());
    }

    [Fact]
    public void RecordFailure_RestartsTheCooldownWindowFromNow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new DiscoveryFailureGate(clock, TimeSpan.FromSeconds(30));

        gate.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(29));
        gate.RecordFailure(); // a second failure during the first window restarts the clock
        clock.Advance(TimeSpan.FromSeconds(29));

        Assert.True(gate.IsInCooldown());
    }

    [Fact]
    public void DefaultCooldown_IsThirtySeconds()
    {
        // #223 item 1: the coordinator-specified production default -- asserted via the
        // constructor's cooldown-omitted overload rather than a magic-number duplication.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new DiscoveryFailureGate(clock);

        gate.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(29.9));
        Assert.True(gate.IsInCooldown());

        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(gate.IsInCooldown());
    }
}

using Microsoft.Extensions.Hosting;

namespace Backend.Sessions;

/// <summary>
/// Hosts <see cref="SessionManager.RunSweepLoopAsync"/> as a real <see cref="BackgroundService"/>
/// instead of an unobserved <c>_ = Task.Run(...)</c>. The host now owns this loop's lifecycle:
/// started once during host startup and stopped via <see cref="BackgroundService"/>'s own
/// cooperative shutdown, which is driven by the same application-stopping signal
/// <c>app.Lifetime.ApplicationStopping</c> used to use directly -- so the sweep still stops
/// cleanly on shutdown, just through the host's normal hosted-service machinery instead of a
/// hand-wired cancellation token.
/// </summary>
internal sealed class SessionSweepService(SessionManager sessionManager) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        sessionManager.RunSweepLoopAsync(stoppingToken);
}

using Microsoft.Extensions.Hosting;

namespace Backend.Sessions;

/// <summary>
/// Hosts <see cref="SessionManager.RunSweepLoopAsync"/> as a <see cref="BackgroundService"/> so
/// its lifecycle (start on host startup, cooperative shutdown on the application-stopping signal)
/// is managed by the host's normal hosted-service machinery rather than a hand-wired cancellation
/// token.
/// </summary>
internal sealed class SessionSweepService(SessionManager sessionManager) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        sessionManager.RunSweepLoopAsync(stoppingToken);
}

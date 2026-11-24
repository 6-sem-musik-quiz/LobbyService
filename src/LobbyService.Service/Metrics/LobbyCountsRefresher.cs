using LobbyService.Repository.Interfaces;
using LobbyService.Repository.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LobbyService.Service.Metrics;

// Reads the lobby counts from Redis every 20 seconds and hands them to LobbyMetrics, so the
// gauge callback (which must be synchronous) never calls Redis itself.
public class LobbyCountsRefresher(IServiceScopeFactory scopes, LobbyMetrics metrics, ILogger<LobbyCountsRefresher> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RefreshAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ILobbyCacheRepository>();
            metrics.Update(await repository.CountByStatusAsync());
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read lobby counts from Redis; slopster_lobbies_active reports nothing.");
            metrics.Update(null);
        }
    }
}

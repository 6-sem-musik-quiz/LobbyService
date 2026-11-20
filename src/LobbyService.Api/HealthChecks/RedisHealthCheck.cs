using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace LobbyService.Api.HealthChecks;

// Ready only when Redis answers PING.
public class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"PING took {latency.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis did not answer PING", ex);
        }
    }
}

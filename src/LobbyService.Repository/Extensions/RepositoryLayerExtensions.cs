using LobbyService.Repository.Interfaces;
using LobbyService.Repository.Repositories;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace LobbyService.Repository.Extensions;

public static class RepositoryLayerExtensions
{
    // LobbyService has no SQL database in the C4 design (only UserDB, StatsDB and MusicDB do).
    // Active lobbies live in Redis.
    public static IServiceCollection AddRepositoryLayer(this IServiceCollection services, string? redisConnectionString)
    {
        // Resolved lazily, so the app (and /health) starts even when Redis is unreachable.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConnectionString ?? "localhost:6379");
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddScoped<ILobbyCacheRepository, LobbyCacheRepository>();
        return services;
    }
}

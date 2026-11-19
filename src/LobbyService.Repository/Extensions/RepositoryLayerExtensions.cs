using Microsoft.Extensions.DependencyInjection;

namespace LobbyService.Repository.Extensions;

public static class RepositoryLayerExtensions
{
    // LobbyService has no SQL database in the C4 design (only UserDB, StatsDB and MusicDB do).
    // Active lobbies go in Redis (LobbyCacheRepository) when that is built.
    public static IServiceCollection AddRepositoryLayer(this IServiceCollection services)
    {
        return services;
    }
}

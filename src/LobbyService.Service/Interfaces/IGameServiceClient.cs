using LobbyService.Domain.Dtos;

namespace LobbyService.Service.Interfaces;

public interface IGameServiceClient
{
    // Calls GameService POST /api/games with the host's token and returns the new game id.
    // Throws GameServiceUnavailableException if GameService fails or cannot be reached.
    Task<Guid> CreateGameAsync(CreateGameRequest request, string accessToken, CancellationToken cancellationToken = default);
}

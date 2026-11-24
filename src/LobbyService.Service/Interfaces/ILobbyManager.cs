using LobbyService.Domain.Dtos;

namespace LobbyService.Service.Interfaces;

public interface ILobbyManager
{
    // Creates a lobby with the user as host and first player.
    // Throws LobbyValidationException (400) or LobbyConflictException (409).
    Task<LobbyDto> CreateAsync(CreateLobbyRequest request, int userId, string userName, string accessToken,
        CancellationToken cancellationToken = default);

    // Public lobbies waiting for players (full ones included), newest first.
    Task<IReadOnlyList<LobbySummaryDto>> GetOpenLobbiesAsync();

    // Full lobby details. Throws LobbyNotFoundException (404) if the lobby does not exist,
    // or if it is private and the user is not one of its players.
    Task<LobbyDto> GetByIdAsync(Guid lobbyId, int userId);
}

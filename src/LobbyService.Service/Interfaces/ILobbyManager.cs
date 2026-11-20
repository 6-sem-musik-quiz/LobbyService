using LobbyService.Domain.Dtos;

namespace LobbyService.Service.Interfaces;

public interface ILobbyManager
{
    // Creates a lobby with the user as host and first player.
    // Throws LobbyValidationException (400) or LobbyConflictException (409).
    Task<LobbyDto> CreateAsync(CreateLobbyRequest request, int userId, string userName, string accessToken,
        CancellationToken cancellationToken = default);
}

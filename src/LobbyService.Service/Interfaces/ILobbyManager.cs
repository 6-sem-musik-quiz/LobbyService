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

    // Joins a public lobby; the player is added last with IsReady = false. Joining a lobby the user is
    // already in returns it unchanged. Throws LobbyNotFoundException (unknown or private lobby) or
    // LobbyConflictException (full, game started, or the user is in another lobby).
    Task<LobbyDto> JoinAsync(Guid lobbyId, int userId, string userName);

    // Like JoinAsync, but finds the lobby by its code (case-insensitive). Private lobbies are allowed.
    // Throws LobbyCodeNotFoundException for an unknown code.
    Task<LobbyDto> JoinByCodeAsync(string code, int userId, string userName);

    // Removes the user. If the host leaves, the longest-staying player becomes host; if the last
    // player leaves, the lobby is deleted. Throws LobbyNotFoundException (unknown lobby or the user
    // is not in it) or LobbyConflictException (the game is running).
    Task LeaveAsync(Guid lobbyId, int userId);
}

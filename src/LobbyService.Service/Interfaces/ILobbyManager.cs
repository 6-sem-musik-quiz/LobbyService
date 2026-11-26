using LobbyService.Domain.Dtos;

namespace LobbyService.Service.Interfaces;

public interface ILobbyManager
{
    // Creates a lobby with the user as host and first player.
    // Throws LobbyValidationException (400) or LobbyConflictException (409).
    Task<LobbyDto> CreateAsync(CreateLobbyRequest request, int userId, string userName, string accessToken,
        CancellationToken cancellationToken = default);

    // The waiting lobby the user is in, or null. A user-lobby key whose lobby is gone, has started or no
    // longer lists the user is deleted here, so the user is free to create or join again.
    Task<LobbyDto?> GetMyLobbyAsync(int userId);

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

    // Sets the user's ready flag. Throws LobbyNotFoundException (unknown lobby or the user is not in it)
    // or LobbyConflictException (the game has started).
    Task<LobbyDto> SetReadyAsync(Guid lobbyId, int userId, bool ready);

    // Host starts the game: GameService POST /api/games is called with the host's token, then the lobby
    // becomes InGame, leaves lobbies:public and all players' user-lobby keys are deleted.
    // Throws LobbyNotFoundException (404), LobbyForbiddenException (not host, 403),
    // LobbyConflictException (started, fewer than 2 players or not all ready, 409) or
    // GameServiceUnavailableException (502, the lobby stays Waiting).
    Task<StartGameResponse> StartAsync(Guid lobbyId, int userId, string accessToken,
        CancellationToken cancellationToken = default);

    // SCRUM-172, called for GameService's GameFinished message: an InGame lobby whose GameId is gameId goes
    // back to Waiting with no players; the game's players wait in PendingReturn. Returns false (and changes
    // nothing) when the lobby is gone or not in that game, so a repeated message is harmless.
    Task<bool> ReopenAfterGameAsync(Guid lobbyId, Guid gameId);

    // SCRUM-172, "Spil igen": moves the user from PendingReturn back into the lobby, not ready. The first
    // player back becomes host if the old host is not there. Returns the lobby unchanged if the user is
    // already back. Throws LobbyConflictException (409: the game has not finished yet, the lobby is full or
    // the user is in another lobby) or LobbyNotFoundException (404: unknown lobby, or the user did not play
    // the game).
    Task<LobbyDto> PlayAgainAsync(Guid lobbyId, int userId, string userName);
}

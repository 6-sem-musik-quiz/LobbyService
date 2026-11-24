using LobbyService.Domain.Entities;

namespace LobbyService.Repository.Interfaces;

public enum CreateLobbyResult
{
    Created,
    CodeTaken,
    UserAlreadyInLobby
}

public enum LobbyUpdateResult
{
    Updated,

    // The lobby changed or disappeared after it was read. Re-read and try again.
    Stale,

    // The joining user already has a user-lobby key (another lobby).
    UserInOtherLobby
}

// Reads and writes active lobbies in Redis.
public interface ILobbyCacheRepository
{
    Task<Lobby?> GetAsync(Guid lobbyId);

    // Id of the lobby with the given (already normalised) code, or null.
    Task<Guid?> GetLobbyIdByCodeAsync(string code);

    // Compare-and-swap: replaces `before` with `after` and sets the user's user-lobby key, but only if
    // the stored lobby still equals `before` and the user has no user-lobby key. Renews all TTLs.
    Task<LobbyUpdateResult> TryJoinAsync(Lobby before, Lobby after, int userId);

    // Compare-and-swap: removes the user's user-lobby key and replaces `before` with `after`.
    // If `after` is null the lobby is deleted together with its code and its lobbies:public entry.
    Task<LobbyUpdateResult> TryLeaveAsync(Lobby before, Lobby? after, int userId);

    // Compare-and-swap: replaces `before` with `after` (e.g. a changed ready flag) if the stored lobby
    // still equals `before`. Renews all TTLs.
    Task<LobbyUpdateResult> TryUpdateAsync(Lobby before, Lobby after);

    // Compare-and-swap for a started game: stores `after` (status InGame) with StartedLobbyTtl, removes it
    // from lobbies:public, deletes its code and every player's user-lobby key, so the players can join
    // new lobbies. Nothing is written if the stored lobby no longer equals `before`.
    Task<LobbyUpdateResult> TryStartAsync(Lobby before, Lobby after);

    // Id of the lobby the user is currently in, or null.
    Task<Guid?> GetLobbyIdForUserAsync(int userId);

    // Atomically stores the lobby, its private code (if any) and the host's user-lobby key.
    // Nothing is written if the code is already taken or the host is already in a lobby.
    // Public waiting lobbies are also added to the lobbies:public set.
    Task<CreateLobbyResult> TryCreateAsync(Lobby lobby);

    // All lobbies in the lobbies:public set that still exist and are public and waiting.
    // Stale ids (expired, private or started lobbies) are removed from the set.
    Task<IReadOnlyList<Lobby>> GetPublicWaitingAsync();

    // Removes the lobby from the lobbies:public set. Call when it starts, becomes private or is deleted.
    Task RemoveFromPublicAsync(Guid lobbyId);
}

using LobbyService.Domain.Entities;
using LobbyService.Repository.Metrics;

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

    // Compare-and-swap for a started game: stores `after` (status InGame) for as long as the game can take
    // (StartedLobbyTtlFor), removes it from lobbies:public and deletes every player's user-lobby key, so the
    // players can join new lobbies. The code is kept for the reopened lobby (SCRUM-172).
    // Nothing is written if the stored lobby no longer equals `before`.
    Task<LobbyUpdateResult> TryStartAsync(Lobby before, Lobby after);

    // SCRUM-172, compare-and-swap after GameFinished: stores `after` (Waiting, no players, the old players in
    // PendingReturn) with the idle TTL and renews the code's TTL. Nothing is written if the stored lobby no
    // longer equals `before`.
    Task<LobbyUpdateResult> TryReopenAsync(Lobby before, Lobby after);

    // Id of the lobby the user is currently in, or null. The key can be stale (see ClearUserLobbyAsync).
    Task<Guid?> GetLobbyIdForUserAsync(int userId);

    // Deletes the user's user-lobby key, but only while it still points at lobbyId, so a lobby the user
    // has joined in the meantime is not touched. Used to clean up a key whose lobby is gone or no longer
    // has the user as a player.
    Task ClearUserLobbyAsync(int userId, Guid lobbyId);

    // Atomically stores the lobby, its private code (if any) and the host's user-lobby key.
    // Nothing is written if the code is already taken or the host is already in a lobby.
    // Public waiting lobbies are also added to the lobbies:public set.
    Task<CreateLobbyResult> TryCreateAsync(Lobby lobby);

    // All lobbies in the lobbies:public set that still exist and are public and waiting.
    // Stale ids (expired, private or started lobbies) are removed from the set.
    Task<IReadOnlyList<Lobby>> GetPublicWaitingAsync();

    // Removes the lobby from the lobbies:public set. Call when it starts, becomes private or is deleted.
    Task RemoveFromPublicAsync(Guid lobbyId);

    // Number of waiting and started (InGame) lobbies, read from two Redis sorted sets
    // (lobbies:waiting, lobbies:ingame) without SCAN. Used by the slopster_lobbies_active gauge.
    Task<LobbyCounts> CountByStatusAsync();
}

using LobbyService.Domain.Entities;

namespace LobbyService.Repository.Interfaces;

public enum CreateLobbyResult
{
    Created,
    CodeTaken,
    UserAlreadyInLobby
}

// Reads and writes active lobbies in Redis.
public interface ILobbyCacheRepository
{
    Task<Lobby?> GetAsync(Guid lobbyId);

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

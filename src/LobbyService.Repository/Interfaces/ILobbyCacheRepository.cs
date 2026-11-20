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
    Task<CreateLobbyResult> TryCreateAsync(Lobby lobby);
}

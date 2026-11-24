using LobbyService.Domain.Dtos;

namespace LobbyService.Service.Hubs;

// Messages the server sends to the clients in a lobby group (lobby:{id}).
public interface ILobbyClient
{
    // After join, leave, ready change and host change.
    Task LobbyUpdated(LobbyDto lobby);

    // When the host has started the game. Payload: { gameId }.
    Task GameStarted(StartGameResponse game);

    // When the lobby is deleted (the last player left).
    Task LobbyClosed();
}

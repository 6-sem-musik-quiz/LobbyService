using LobbyService.Repository.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace LobbyService.Service.Hubs;

// SignalR hub at /hubs/lobby. Clients only subscribe to a lobby's group here; every change to a lobby
// goes through the REST API, and LobbyManager pushes the result via IHubContext.
// OnDisconnectedAsync is deliberately not overridden: a dropped connection does not change the lobby,
// so the player can reconnect and call JoinLobbyGroup again.
public class LobbyHub(ILobbyCacheRepository repository) : Hub<ILobbyClient>
{
    public static string GroupName(Guid lobbyId) => $"lobby:{lobbyId}";

    public async Task JoinLobbyGroup(Guid lobbyId)
    {
        if (!int.TryParse(Context.User?.FindFirst("sub")?.Value, out var userId))
            throw new HubException("The token has no valid user id.");

        var lobby = await repository.GetAsync(lobbyId);
        if (lobby is null || lobby.Players.All(p => p.UserId != userId))
            throw new HubException("You are not in this lobby.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(lobbyId));
    }

    public Task LeaveLobbyGroup(Guid lobbyId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(lobbyId));
}

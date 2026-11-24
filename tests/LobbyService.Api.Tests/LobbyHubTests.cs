using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace LobbyService.Api.Tests;

// End-to-end tests of /hubs/lobby against the TestServer: changes go through REST, updates arrive over SignalR.
public class LobbyHubTests(LobbyApiFactory factory) : IClassFixture<LobbyApiFactory>, IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
    }

    private HttpClient ClientFor(int userId, string userName)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", LobbyApiFactory.CreateToken(userId, userName));
        return client;
    }

    // WebSockets like a browser: negotiate sends the token as a Bearer header, the WebSocket itself
    // sends it as ?access_token= (browsers cannot set headers on WebSockets).
    private HubConnection ConnectionFor(string? token, string path = "/hubs/lobby")
    {
        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token);
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri);
                    if (token is not null)
                        uri.Query = $"{uri.Query.TrimStart('?')}&access_token={Uri.EscapeDataString(token)}";
                    return await server.CreateWebSocketClient().ConnectAsync(uri.Uri, cancellationToken);
                };
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private async Task<(Guid LobbyId, HubConnection Host)> CreateLobbyWithConnectedHost(int hostId)
    {
        var create = await ClientFor(hostId, "host").PostAsJsonAsync("/api/lobbies", new
        {
            categoryId = LobbyApiFactory.KnownPlaylistId, answerTimeSeconds = 30, rounds = 10, maxPlayers = 4,
            isPrivate = false
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var lobbyId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var host = ConnectionFor(LobbyApiFactory.CreateToken(hostId, "host"));
        await host.StartAsync();
        await host.InvokeAsync("JoinLobbyGroup", lobbyId);
        return (lobbyId, host);
    }

    private static TaskCompletionSource<T> Listen<T>(HubConnection connection, string method)
    {
        var received = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<T>(method, value => received.TrySetResult(value));
        return received;
    }

    [Fact]
    public async Task OtherPlayerJoins_ConnectedPlayerReceivesLobbyUpdated()
    {
        var (lobbyId, host) = await CreateLobbyWithConnectedHost(3001);
        var updated = Listen<JsonElement>(host, "LobbyUpdated");

        // Client B also has a hub connection, then joins through the REST API.
        var other = ConnectionFor(LobbyApiFactory.CreateToken(3002, "bob"));
        await other.StartAsync();
        var join = await ClientFor(3002, "bob").PostAsync($"/api/lobbies/{lobbyId}/join", null);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);

        var lobby = await updated.Task.WaitAsync(Timeout);
        Assert.Equal(lobbyId, lobby.GetProperty("id").GetGuid());
        Assert.Equal("Waiting", lobby.GetProperty("status").GetString());
        var players = lobby.GetProperty("players").EnumerateArray().ToList();
        Assert.Equal(2, players.Count);
        Assert.Equal("bob", players[1].GetProperty("userName").GetString());

        // Once in the lobby, B may subscribe to the group as well.
        await other.InvokeAsync("JoinLobbyGroup", lobbyId);
    }

    // The edge forwards /api/lobbyservice/hubs/lobby as /api/hubs/lobby; that path must work too,
    // including the ?access_token= on the WebSocket.
    [Fact]
    public async Task HubBehindTheEdgePath_Works()
    {
        var create = await ClientFor(3051, "host").PostAsJsonAsync("/api/lobbies", new
        {
            categoryId = LobbyApiFactory.KnownPlaylistId, answerTimeSeconds = 30, rounds = 10, maxPlayers = 4,
            isPrivate = false
        });
        var lobbyId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var host = ConnectionFor(LobbyApiFactory.CreateToken(3051, "host"), "/api/hubs/lobby");
        await host.StartAsync();
        await host.InvokeAsync("JoinLobbyGroup", lobbyId);
        var updated = Listen<JsonElement>(host, "LobbyUpdated");

        await ClientFor(3052, "bob").PostAsync($"/api/lobbies/{lobbyId}/join", null);

        var lobby = await updated.Task.WaitAsync(Timeout);
        Assert.Equal(2, lobby.GetProperty("players").GetArrayLength());
    }

    [Fact]
    public async Task HostStartsGame_PlayersReceiveGameStarted()
    {
        var (lobbyId, host) = await CreateLobbyWithConnectedHost(3011);
        var guest = ClientFor(3012, "guest");
        await guest.PostAsync($"/api/lobbies/{lobbyId}/join", null);
        var guestConnection = ConnectionFor(LobbyApiFactory.CreateToken(3012, "guest"));
        await guestConnection.StartAsync();
        await guestConnection.InvokeAsync("JoinLobbyGroup", lobbyId);
        var started = Listen<JsonElement>(guestConnection, "GameStarted");

        var hostClient = ClientFor(3011, "host");
        await hostClient.PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });
        await guest.PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });
        var start = await hostClient.PostAsync($"/api/lobbies/{lobbyId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var gameId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gameId").GetGuid();

        var message = await started.Task.WaitAsync(Timeout);
        Assert.Equal(gameId, message.GetProperty("gameId").GetGuid());
    }

    [Fact]
    public async Task LastPlayerLeaves_ReceivesLobbyClosed()
    {
        var (lobbyId, host) = await CreateLobbyWithConnectedHost(3021);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("LobbyClosed", () => closed.TrySetResult());

        var leave = await ClientFor(3021, "host").PostAsync($"/api/lobbies/{lobbyId}/leave", null);
        Assert.True(leave.IsSuccessStatusCode);

        await closed.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task JoinLobbyGroup_NotInLobby_ThrowsHubException()
    {
        var (lobbyId, _) = await CreateLobbyWithConnectedHost(3031);
        var outsider = ConnectionFor(LobbyApiFactory.CreateToken(3032, "eve"));
        await outsider.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() => outsider.InvokeAsync("JoinLobbyGroup", lobbyId));
        Assert.Contains("not in this lobby", ex.Message);
    }

    [Fact]
    public async Task Connect_WithoutToken_IsRejected()
    {
        var connection = ConnectionFor(null);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Connect_WithInvalidToken_IsRejected()
    {
        var connection = ConnectionFor(LobbyApiFactory.CreateToken(3041, "mallory",
            signingKey: "another-signing-key-that-the-service-does-not-trust-0123456789"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }
}

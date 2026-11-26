using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LobbyService.Repository.Interfaces;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace LobbyService.Api.Tests;

// SCRUM-128: two LobbyService pods, two players, one lobby. Each "pod" is its own WebApplicationFactory
// (own process state, own SignalR connections); they share the lobby store, like two pods share Redis.
// The host is connected to pod A, the guest to pod B, and each changes the lobby through its own pod.
//
// WithoutBackplane always runs and shows the problem. WithRedisBackplane needs a real Redis and is
// skipped unless LOBBY_TEST_REDIS is set, e.g.
//   docker run -d --rm -p 6379:6379 --name lobby-test-redis redis:7-alpine
//   LOBBY_TEST_REDIS=localhost:6379 dotnet test -c Release --filter BackplaneTests
public class BackplaneTests : IAsyncLifetime
{
    private static readonly string? Redis = Environment.GetEnvironmentVariable("LOBBY_TEST_REDIS");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly List<IAsyncDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
            await disposable.DisposeAsync();
    }

    [RedisFact]
    public async Task WithRedisBackplane_PlayersOnDifferentPodsSeeEachOthersEvents()
    {
        var (podA, podB) = TwoPods(Redis);
        var (lobbyId, host, guest) = await HostOnAGuestOnB(podA, podB, 4001, 4002);

        // Guest gets ready on pod B; the host on pod A must hear it.
        var hostHears = Listen(host, "LobbyUpdated");
        await Client(podB, 4002, "guest").PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });
        var seenByHost = await hostHears.Task.WaitAsync(Timeout);
        Assert.True(Player(seenByHost, 4002).GetProperty("isReady").GetBoolean());

        // And the other way: host gets ready on pod A; the guest on pod B must hear it.
        var guestHears = Listen(guest, "LobbyUpdated");
        await Client(podA, 4001, "host").PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });
        var seenByGuest = await guestHears.Task.WaitAsync(Timeout);
        Assert.True(Player(seenByGuest, 4001).GetProperty("isReady").GetBoolean());
    }

    // The bug SCRUM-128 fixes: without a backplane pod B only sends to its own connections,
    // so the host on pod A never hears that the guest is ready.
    [Fact]
    public async Task WithoutBackplane_PlayerOnOtherPodMissesTheEvent()
    {
        var (podA, podB) = TwoPods(redis: null);
        var (lobbyId, host, _) = await HostOnAGuestOnB(podA, podB, 4101, 4102);

        var hostHears = Listen(host, "LobbyUpdated");
        var ready = await Client(podB, 4102, "guest").PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var winner = await Task.WhenAny(hostHears.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(hostHears.Task, winner);
    }

    private (LobbyApiFactory PodA, LobbyApiFactory PodB) TwoPods(string? redis)
    {
        var store = new LobbyApiFactory.InMemoryLobbyCacheRepository();
        var podA = new Pod(store, redis);
        var podB = new Pod(store, redis);
        _disposables.Add(podA);
        _disposables.Add(podB);
        return (podA, podB);
    }

    // Host creates the lobby and connects on pod A; guest joins and connects on pod B.
    private async Task<(Guid LobbyId, HubConnection Host, HubConnection Guest)> HostOnAGuestOnB(
        LobbyApiFactory podA, LobbyApiFactory podB, int hostId, int guestId)
    {
        var create = await Client(podA, hostId, "host").PostAsJsonAsync("/api/lobbies", new
        {
            categoryId = LobbyApiFactory.KnownPlaylistId, answerTimeSeconds = 30, rounds = 10, maxPlayers = 4,
            isPrivate = false
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var lobbyId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var join = await Client(podB, guestId, "guest").PostAsync($"/api/lobbies/{lobbyId}/join", null);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);

        var host = await Connect(podA, hostId, "host", lobbyId);
        var guest = await Connect(podB, guestId, "guest", lobbyId);
        return (lobbyId, host, guest);
    }

    private static HttpClient Client(LobbyApiFactory pod, int userId, string userName)
    {
        var client = pod.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", LobbyApiFactory.CreateToken(userId, userName));
        return client;
    }

    // Like the frontend after SCRUM-128: no negotiate, straight to the WebSocket, token as ?access_token=.
    private async Task<HubConnection> Connect(LobbyApiFactory pod, int userId, string userName, Guid lobbyId)
    {
        var server = pod.Server;
        var token = LobbyApiFactory.CreateToken(userId, userName);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/lobby"), options =>
            {
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri);
                    uri.Query = $"{uri.Query.TrimStart('?')}&access_token={Uri.EscapeDataString(token)}";
                    return await server.CreateWebSocketClient().ConnectAsync(uri.Uri, cancellationToken);
                };
            })
            .Build();
        _disposables.Add(connection);
        await connection.StartAsync();
        await connection.InvokeAsync("JoinLobbyGroup", lobbyId);
        return connection;
    }

    private static TaskCompletionSource<JsonElement> Listen(HubConnection connection, string method)
    {
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(method, value => received.TrySetResult(value));
        return received;
    }

    private static JsonElement Player(JsonElement lobby, int userId) =>
        lobby.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("userId").GetInt32() == userId);

    // One LobbyService pod: shares the lobby store with the other pod and, with redis set, the backplane.
    private sealed class Pod(ILobbyCacheRepository store, string? redis) : LobbyApiFactory
    {
        protected override ILobbyCacheRepository SharedRepository => store;
        protected override string? BackplaneRedis => redis;
    }

    // Skipped unless LOBBY_TEST_REDIS points at a Redis (CI has none).
    private sealed class RedisFactAttribute : FactAttribute
    {
        public RedisFactAttribute()
        {
            if (string.IsNullOrEmpty(Redis))
                Skip = "Set LOBBY_TEST_REDIS (e.g. localhost:6379) to run the two-pod Redis backplane test.";
        }
    }
}

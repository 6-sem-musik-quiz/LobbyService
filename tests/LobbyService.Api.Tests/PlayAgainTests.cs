using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LobbyService.Service.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace LobbyService.Api.Tests;

// SCRUM-172: "Spil igen". The whole flow through the API: a game is started, GameService's GameFinished
// message arrives (handed to GameFinishedHandler exactly as the RabbitMQ consumer does), the players press
// "Spil igen" and a new game is started from the same lobby.
public class PlayAgainTests(LobbyApiFactory factory) : IClassFixture<LobbyApiFactory>
{
    private HttpClient ClientFor(int userId, string userName)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", LobbyApiFactory.CreateToken(userId, userName));
        return client;
    }

    // What GameService publishes (contract gameservice.game-finished 1.1), camelCase JSON.
    private async Task<DeliveryOutcome> GameFinished(Guid gameId, Guid? lobbyId)
    {
        var json = JsonSerializer.Serialize(new
        {
            gameId, lobbyId, rounds = 5, startTime = DateTime.UtcNow.AddMinutes(-5), endTime = DateTime.UtcNow,
            catId = LobbyApiFactory.KnownPlaylistId, categoryName = "Test playlist",
            players = new[] { new { userId = 1, userName = "x", score = 1, correctAnswers = 1, answeredRounds = 1, placement = 1 } }
        });
        var handler = factory.Services.GetRequiredService<GameFinishedHandler>();
        return await handler.HandleAsync(Encoding.UTF8.GetBytes(json));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static int[] PlayerIds(JsonElement lobby) =>
        lobby.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("userId").GetInt32()).ToArray();

    // Creates a lobby, lets the guests join, gets everyone ready and starts the game.
    private async Task<(Guid LobbyId, string? Code, Guid GameId)> PlayAGame(bool isPrivate, int hostId, params int[] guestIds)
    {
        var host = ClientFor(hostId, $"p{hostId}");
        var create = await host.PostAsJsonAsync("/api/lobbies", new
        {
            categoryId = LobbyApiFactory.KnownPlaylistId, answerTimeSeconds = 30, rounds = 10, maxPlayers = 4, isPrivate
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var lobby = await Json(create);
        var lobbyId = lobby.GetProperty("id").GetGuid();
        var code = isPrivate ? lobby.GetProperty("code").GetString() : null;

        foreach (var guestId in guestIds)
        {
            var join = isPrivate
                ? await ClientFor(guestId, $"p{guestId}").PostAsJsonAsync("/api/lobbies/join-by-code", new { code })
                : await ClientFor(guestId, $"p{guestId}").PostAsync($"/api/lobbies/{lobbyId}/join", null);
            Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        }

        foreach (var id in guestIds.Prepend(hostId))
            await ClientFor(id, $"p{id}").PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });

        var start = await host.PostAsync($"/api/lobbies/{lobbyId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        return (lobbyId, code, (await Json(start)).GetProperty("gameId").GetGuid());
    }

    [Fact]
    public async Task GameEnds_LobbyReopens_PlayersComeBack_AndANewGameStarts()
    {
        var (lobbyId, code, firstGame) = await PlayAGame(isPrivate: true, 5001, 5002);
        var host = ClientFor(5001, "p5001");
        var guest = ClientFor(5002, "p5002");

        // The game is still running: not yet.
        var tooEarly = await host.PostAsync($"/api/lobbies/{lobbyId}/play-again", null);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);

        // GameService: the game is over.
        Assert.Equal(DeliveryOutcome.Ack, await GameFinished(firstGame, lobbyId));

        // "Spil igen": same lobby, same code, same settings, not ready.
        var hostBack = await host.PostAsync($"/api/lobbies/{lobbyId}/play-again", null);
        Assert.Equal(HttpStatusCode.OK, hostBack.StatusCode);
        var guestBack = await guest.PostAsync($"/api/lobbies/{lobbyId}/play-again", null);
        Assert.Equal(HttpStatusCode.OK, guestBack.StatusCode);

        var lobby = await Json(guestBack);
        Assert.Equal("Waiting", lobby.GetProperty("status").GetString());
        Assert.Equal(code, lobby.GetProperty("code").GetString());
        Assert.Equal(5001, lobby.GetProperty("hostUserId").GetInt32());
        Assert.Equal([5001, 5002], PlayerIds(lobby));
        Assert.All(lobby.GetProperty("players").EnumerateArray(), p => Assert.False(p.GetProperty("isReady").GetBoolean()));
        Assert.Equal(10, lobby.GetProperty("settings").GetProperty("rounds").GetInt32());
        Assert.Equal(30, lobby.GetProperty("settings").GetProperty("answerTimeSeconds").GetInt32());

        // A new player joins with the same code.
        var newcomer = ClientFor(5003, "p5003");
        var join = await newcomer.PostAsJsonAsync("/api/lobbies/join-by-code", new { code });
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        Assert.Equal(new[] { 5001, 5002, 5003 }, PlayerIds(await Json(join)));

        // Not everyone is ready yet, then a new game starts as normal and gets a new gameId.
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsync($"/api/lobbies/{lobbyId}/start", null)).StatusCode);
        foreach (var client in new[] { host, guest, newcomer })
            await client.PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });
        var start = await host.PostAsync($"/api/lobbies/{lobbyId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.NotEqual(firstGame, (await Json(start)).GetProperty("gameId").GetGuid());
    }

    [Fact]
    public async Task APlayerWhoLeaves_IsNotInTheReopenedLobby_AndTheFirstOneBackBecomesHost()
    {
        var (lobbyId, _, game) = await PlayAGame(isPrivate: false, 5101, 5102, 5103);
        await GameFinished(game, lobbyId);

        // The host pressed "Forlad" (never comes back); 5103 is back first, then 5102.
        var first = await ClientFor(5103, "p5103").PostAsync($"/api/lobbies/{lobbyId}/play-again", null);
        var second = await ClientFor(5102, "p5102").PostAsync($"/api/lobbies/{lobbyId}/play-again", null);

        var lobby = await Json(second);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal([5103, 5102], PlayerIds(lobby));
        Assert.Equal(5103, lobby.GetProperty("hostUserId").GetInt32());

        // The new host can start with the two that came back.
        foreach (var id in new[] { 5103, 5102 })
            await ClientFor(id, $"p{id}").PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });
        var start = await ClientFor(5103, "p5103").PostAsync($"/api/lobbies/{lobbyId}/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
    }

    [Fact]
    public async Task AReopenedPublicLobby_IsListedOnlyOnceSomeoneIsBack()
    {
        var (lobbyId, _, game) = await PlayAGame(isPrivate: false, 5201, 5202);
        await GameFinished(game, lobbyId);
        var outsider = ClientFor(5299, "outsider");

        bool Listed(JsonElement list) => list.EnumerateArray().Any(l => l.GetProperty("id").GetGuid() == lobbyId);

        Assert.False(Listed(await Json(await outsider.GetAsync("/api/lobbies"))));
        await ClientFor(5202, "p5202").PostAsync($"/api/lobbies/{lobbyId}/play-again", null);
        var list = await Json(await outsider.GetAsync("/api/lobbies"));
        Assert.True(Listed(list));
        Assert.Equal("p5202", list.EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == lobbyId)
            .GetProperty("hostUserName").GetString());
    }

    [Fact]
    public async Task OnlyPlayersOfTheGame_CanPlayAgain()
    {
        var (lobbyId, _, game) = await PlayAGame(isPrivate: true, 5301, 5302);
        await GameFinished(game, lobbyId);

        var outsider = await ClientFor(5399, "outsider").PostAsync($"/api/lobbies/{lobbyId}/play-again", null);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        var unknown = await ClientFor(5301, "p5301").PostAsync($"/api/lobbies/{Guid.NewGuid()}/play-again", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task PressingPlayAgainTwice_ReturnsTheSameLobby()
    {
        var (lobbyId, _, game) = await PlayAGame(isPrivate: true, 5401, 5402);
        await GameFinished(game, lobbyId);
        var host = ClientFor(5401, "p5401");

        await host.PostAsync($"/api/lobbies/{lobbyId}/play-again", null);
        var again = await host.PostAsync($"/api/lobbies/{lobbyId}/play-again", null);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(new[] { 5401 }, PlayerIds(await Json(again)));
    }

    // RabbitMQ delivers at least once: the same message twice must not reopen the lobby a second time
    // (which would throw out the players who are already back).
    [Fact]
    public async Task ARepeatedGameFinished_ChangesNothing()
    {
        var (lobbyId, _, game) = await PlayAGame(isPrivate: true, 5501, 5502);
        await GameFinished(game, lobbyId);
        await ClientFor(5501, "p5501").PostAsync($"/api/lobbies/{lobbyId}/play-again", null);

        Assert.Equal(DeliveryOutcome.Ack, await GameFinished(game, lobbyId));

        var lobby = await Json(await ClientFor(5501, "p5501").GetAsync($"/api/lobbies/{lobbyId}"));
        Assert.Equal([5501], PlayerIds(lobby));
    }

    // A GameService older than SCRUM-172 sends no lobbyId; a broken message is dropped.
    [Fact]
    public async Task MessagesWithoutLobbyId_AreAcked_AndInvalidOnesRejected()
    {
        var (lobbyId, _, game) = await PlayAGame(isPrivate: true, 5601, 5602);

        Assert.Equal(DeliveryOutcome.Ack, await GameFinished(game, lobbyId: null));
        Assert.Equal(HttpStatusCode.Conflict,
            (await ClientFor(5601, "p5601").PostAsync($"/api/lobbies/{lobbyId}/play-again", null)).StatusCode);

        var handler = factory.Services.GetRequiredService<GameFinishedHandler>();
        Assert.Equal(DeliveryOutcome.Reject, await handler.HandleAsync(Encoding.UTF8.GetBytes("not json")));
        Assert.Equal(DeliveryOutcome.Reject, await handler.HandleAsync(Encoding.UTF8.GetBytes("{}")));
    }
}

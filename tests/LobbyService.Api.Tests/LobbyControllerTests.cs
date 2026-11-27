using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace LobbyService.Api.Tests;

public class LobbyControllerTests(LobbyApiFactory factory) : IClassFixture<LobbyApiFactory>
{
    private HttpClient ClientFor(int userId, string userName = "player")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", LobbyApiFactory.CreateToken(userId, userName));
        return client;
    }

    private static object Body(int categoryId = LobbyApiFactory.KnownPlaylistId, int rounds = 10, bool isPrivate = false) => new
    {
        categoryId,
        answerTimeSeconds = 30,
        rounds,
        maxPlayers = 4,
        isPrivate
    };

    [Fact]
    public async Task Create_Valid_Returns201WithLobby()
    {
        var client = ClientFor(1001, "alice");

        var response = await client.PostAsJsonAsync("/api/lobbies", Body(isPrivate: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1001, json.GetProperty("hostUserId").GetInt32());
        Assert.True(json.GetProperty("isPrivate").GetBoolean());
        Assert.Equal(6, json.GetProperty("code").GetString()!.Length);
        Assert.Equal("Waiting", json.GetProperty("status").GetString());
        Assert.Equal("Test playlist", json.GetProperty("settings").GetProperty("categoryName").GetString());
        var player = Assert.Single(json.GetProperty("players").EnumerateArray());
        Assert.Equal("alice", player.GetProperty("userName").GetString());
        Assert.False(player.GetProperty("isReady").GetBoolean());
    }

    [Fact]
    public async Task Create_PublicLobby_HasNoCode()
    {
        var response = await ClientFor(1002).PostAsJsonAsync("/api/lobbies", Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, json.GetProperty("code").ValueKind);
    }

    [Fact]
    public async Task Create_InvalidRounds_Returns400()
    {
        var response = await ClientFor(1003).PostAsJsonAsync("/api/lobbies", Body(rounds: 7));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("rounds", problem!.Detail);
    }

    [Fact]
    public async Task Create_UnknownPlaylist_Returns400()
    {
        var response = await ClientFor(1004).PostAsJsonAsync("/api/lobbies",
            Body(categoryId: LobbyApiFactory.UnknownPlaylistId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/lobbies", Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithTokenSignedByWrongKey_Returns401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            LobbyApiFactory.CreateToken(1005, "mallory", "another-key-that-is-long-enough-for-hs256-0123456789"));

        var response = await client.PostAsJsonAsync("/api/lobbies", Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_UserAlreadyInLobby_Returns409()
    {
        var client = ClientFor(1006);
        var first = await client.PostAsJsonAsync("/api/lobbies", Body());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/lobbies", Body());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task List_Returns200WithPublicLobbies_WithoutPrivate()
    {
        var visible = await (await ClientFor(1102, "host-new").PostAsJsonAsync("/api/lobbies", Body(rounds: 5)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var hidden = await (await ClientFor(1103).PostAsJsonAsync("/api/lobbies", Body(isPrivate: true)))
            .Content.ReadFromJsonAsync<JsonElement>();

        var response = await ClientFor(1104).GetAsync("/api/lobbies");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var ids = rows.Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.DoesNotContain(hidden.GetProperty("id").GetString(), ids);

        var row = rows.Single(r => r.GetProperty("id").GetString() == visible.GetProperty("id").GetString());
        Assert.Equal("host-new", row.GetProperty("hostUserName").GetString());
        Assert.Equal("Test playlist", row.GetProperty("categoryName").GetString());
        Assert.Equal(1, row.GetProperty("playerCount").GetInt32());
        Assert.Equal(4, row.GetProperty("maxPlayers").GetInt32());
        Assert.Equal(5, row.GetProperty("rounds").GetInt32());
        Assert.Equal(30, row.GetProperty("answerTimeSeconds").GetInt32());
    }

    [Fact]
    public async Task List_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/lobbies");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetById_PublicLobby_Returns200WithPlayers()
    {
        var created = await (await ClientFor(1201, "bob").PostAsJsonAsync("/api/lobbies", Body()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var response = await ClientFor(1202).GetAsync($"/api/lobbies/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, json.GetProperty("id").GetString());
        var player = Assert.Single(json.GetProperty("players").EnumerateArray());
        Assert.Equal("bob", player.GetProperty("userName").GetString());
        Assert.False(player.GetProperty("isReady").GetBoolean());
    }

    [Fact]
    public async Task GetById_UnknownLobby_Returns404()
    {
        var response = await ClientFor(1203).GetAsync($"/api/lobbies/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(404, problem!.Status);
    }

    [Fact]
    public async Task GetById_PrivateLobby_Returns404ForOutsiderAnd200ForPlayer()
    {
        var host = ClientFor(1204, "carol");
        var created = await (await host.PostAsJsonAsync("/api/lobbies", Body(isPrivate: true)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor(1205).GetAsync($"/api/lobbies/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/lobbies/{id}")).StatusCode);
    }

    [Fact]
    public async Task GetById_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync($"/api/lobbies/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<JsonElement> CreateLobby(HttpClient host, bool isPrivate = false, int maxPlayers = 4)
    {
        var response = await host.PostAsJsonAsync("/api/lobbies", new
        {
            categoryId = LobbyApiFactory.KnownPlaylistId,
            answerTimeSeconds = 30,
            rounds = 10,
            maxPlayers,
            isPrivate
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Join_PublicLobby_Returns200WithPlayerAddedLast()
    {
        var id = (await CreateLobby(ClientFor(1301, "host"))).GetProperty("id").GetString();

        var response = await ClientFor(1302, "guest").PostAsync($"/api/lobbies/{id}/join", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var players = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("players")
            .EnumerateArray().ToList();
        Assert.Equal(["host", "guest"], players.Select(p => p.GetProperty("userName").GetString()));
        Assert.False(players[1].GetProperty("isReady").GetBoolean());
    }

    [Fact]
    public async Task Join_Twice_Returns200WithoutDuplicate()
    {
        var id = (await CreateLobby(ClientFor(1311))).GetProperty("id").GetString();
        var guest = ClientFor(1312);
        await guest.PostAsync($"/api/lobbies/{id}/join", null);

        var response = await guest.PostAsync($"/api/lobbies/{id}/join", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("players").GetArrayLength());
    }

    [Fact]
    public async Task Join_UnknownLobby_Returns404()
    {
        var response = await ClientFor(1321).PostAsync($"/api/lobbies/{Guid.NewGuid()}/join", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Join_PrivateLobbyById_Returns404()
    {
        var id = (await CreateLobby(ClientFor(1322), isPrivate: true)).GetProperty("id").GetString();

        var response = await ClientFor(1323).PostAsync($"/api/lobbies/{id}/join", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Join_FullLobby_Returns409()
    {
        var id = (await CreateLobby(ClientFor(1331), maxPlayers: 2)).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await ClientFor(1332).PostAsync($"/api/lobbies/{id}/join", null)).StatusCode);

        var response = await ClientFor(1333).PostAsync($"/api/lobbies/{id}/join", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Join_ConcurrentForLastSeat_OnlyOneSucceeds()
    {
        var id = (await CreateLobby(ClientFor(1341), maxPlayers: 2)).GetProperty("id").GetString();

        var responses = await Task.WhenAll(
            Enumerable.Range(1342, 6).Select(u => ClientFor(u).PostAsync($"/api/lobbies/{id}/join", null)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Fact]
    public async Task Join_UserInAnotherLobby_Returns409()
    {
        var other = ClientFor(1351);
        await CreateLobby(other);
        var id = (await CreateLobby(ClientFor(1352))).GetProperty("id").GetString();

        var response = await other.PostAsync($"/api/lobbies/{id}/join", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Join_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().PostAsync($"/api/lobbies/{Guid.NewGuid()}/join", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task JoinByCode_LowercaseCode_Returns200()
    {
        var created = await CreateLobby(ClientFor(1361), isPrivate: true);
        var code = created.GetProperty("code").GetString()!;

        var response = await ClientFor(1362).PostAsJsonAsync("/api/lobbies/join-by-code", new { code = code.ToLowerInvariant() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(created.GetProperty("id").GetString(), json.GetProperty("id").GetString());
        Assert.Equal(2, json.GetProperty("players").GetArrayLength());
    }

    [Fact]
    public async Task JoinByCode_UnknownCode_Returns404()
    {
        var response = await ClientFor(1363).PostAsJsonAsync("/api/lobbies/join-by-code", new { code = "ZZZZZZ" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JoinByCode_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/lobbies/join-by-code", new { code = "ABCDEF" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Leave_HostLeaves_Returns204AndNextPlayerBecomesHost()
    {
        var host = ClientFor(1401);
        var id = (await CreateLobby(host)).GetProperty("id").GetString();
        var guest = ClientFor(1402);
        await guest.PostAsync($"/api/lobbies/{id}/join", null);

        var response = await host.PostAsync($"/api/lobbies/{id}/leave", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var lobby = await (await guest.GetAsync($"/api/lobbies/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1402, lobby.GetProperty("hostUserId").GetInt32());
        Assert.Equal(1, lobby.GetProperty("players").GetArrayLength());

        // The former host is free to create a new lobby.
        await CreateLobby(host);
    }

    [Fact]
    public async Task Leave_LastPlayer_Returns204AndDeletesLobbyAndCode()
    {
        var host = ClientFor(1411);
        var created = await CreateLobby(host, isPrivate: true);
        var id = created.GetProperty("id").GetString();

        var response = await host.PostAsync($"/api/lobbies/{id}/leave", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/lobbies/{id}")).StatusCode);
        var byCode = await ClientFor(1412).PostAsJsonAsync("/api/lobbies/join-by-code",
            new { code = created.GetProperty("code").GetString() });
        Assert.Equal(HttpStatusCode.NotFound, byCode.StatusCode);
    }

    [Fact]
    public async Task Leave_NotInLobby_Returns404()
    {
        var id = (await CreateLobby(ClientFor(1421))).GetProperty("id").GetString();

        var response = await ClientFor(1422).PostAsync($"/api/lobbies/{id}/leave", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Leave_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().PostAsync($"/api/lobbies/{Guid.NewGuid()}/leave", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Ready / start ----

    // Creates a lobby with the host and the given guests, optionally marking everyone ready.
    private async Task<string> LobbyWith(HttpClient host, HttpClient[] guests, bool allReady, bool isPrivate = false)
    {
        var id = (await CreateLobby(host, isPrivate: isPrivate)).GetProperty("id").GetString()!;
        foreach (var guest in guests)
        {
            var join = isPrivate
                ? await guest.PostAsJsonAsync("/api/lobbies/join-by-code",
                    new { code = (await (await host.GetAsync($"/api/lobbies/{id}")).Content.ReadFromJsonAsync<JsonElement>())
                        .GetProperty("code").GetString() })
                : await guest.PostAsync($"/api/lobbies/{id}/join", null);
            Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        }

        if (allReady)
            foreach (var client in guests.Prepend(host))
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsJsonAsync($"/api/lobbies/{id}/ready", new { ready = true })).StatusCode);
        return id;
    }

    [Fact]
    public async Task Ready_TogglesFlag_Returns200WithLobby()
    {
        var host = ClientFor(1501);
        var guest = ClientFor(1502, "guest");
        var id = await LobbyWith(host, [guest], allReady: false);

        var ready = await guest.PostAsJsonAsync($"/api/lobbies/{id}/ready", new { ready = true });
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var players = (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("players").EnumerateArray().ToList();
        Assert.False(players[0].GetProperty("isReady").GetBoolean());
        Assert.True(players[1].GetProperty("isReady").GetBoolean());

        var notReady = await guest.PostAsJsonAsync($"/api/lobbies/{id}/ready", new { ready = false });
        var after = await notReady.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(after.GetProperty("players")[1].GetProperty("isReady").GetBoolean());
    }

    [Fact]
    public async Task Ready_NotInLobby_Returns404()
    {
        var id = await LobbyWith(ClientFor(1511), [], allReady: false);

        var response = await ClientFor(1512).PostAsJsonAsync($"/api/lobbies/{id}/ready", new { ready = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Start_AllReady_Returns200WithGameIdAndFreesPlayers()
    {
        var host = ClientFor(1521, "host");
        var guest = ClientFor(1522, "guest");
        var id = await LobbyWith(host, [guest], allReady: true);

        var response = await host.PostAsync($"/api/lobbies/{id}/start", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var gameId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gameId").GetGuid();
        Assert.NotEqual(Guid.Empty, gameId);

        var lobby = await (await guest.GetAsync($"/api/lobbies/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("InGame", lobby.GetProperty("status").GetString());
        var open = await (await guest.GetAsync("/api/lobbies")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(open.EnumerateArray(), l => l.GetProperty("id").GetString() == id);

        // Both players can create new lobbies after the start.
        await CreateLobby(host);
        await CreateLobby(guest);
    }

    [Fact]
    public async Task Start_NotHost_Returns403()
    {
        var host = ClientFor(1531);
        var guest = ClientFor(1532);
        var id = await LobbyWith(host, [guest], allReady: true);

        var response = await guest.PostAsync($"/api/lobbies/{id}/start", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Start_TooFewPlayers_Returns409()
    {
        var host = ClientFor(1541);
        var id = await LobbyWith(host, [], allReady: true);

        var response = await host.PostAsync($"/api/lobbies/{id}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Start_NotAllReady_Returns409()
    {
        var host = ClientFor(1551);
        var id = await LobbyWith(host, [ClientFor(1552)], allReady: false);
        await host.PostAsJsonAsync($"/api/lobbies/{id}/ready", new { ready = true });

        var response = await host.PostAsync($"/api/lobbies/{id}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Start_Twice_Returns409AndReadyAfterStartReturns409()
    {
        var host = ClientFor(1561);
        var id = await LobbyWith(host, [ClientFor(1562)], allReady: true, isPrivate: true);
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync($"/api/lobbies/{id}/start", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsync($"/api/lobbies/{id}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await host.PostAsJsonAsync($"/api/lobbies/{id}/ready", new { ready = false })).StatusCode);
    }

    // SCRUM-179: GameService switched off by an admin -> 503 with the maintenance problem, lobby stays Waiting.
    [Fact]
    public async Task Start_GameServiceInMaintenance_Returns503MaintenanceProblem_AndLobbyStaysWaiting()
    {
        var host = ClientFor(1581);
        var guest = ClientFor(LobbyApiFactory.GameServiceInMaintenanceForUserId);
        var id = await LobbyWith(host, [guest], allReady: true);

        var response = await host.PostAsync($"/api/lobbies/{id}/start", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://quiz.munkcloud.dk/problems/maintenance", problem.GetProperty("type").GetString());
        Assert.Equal("Back at 20:00", problem.GetProperty("detail").GetString());
        Assert.Equal("gameservice", problem.GetProperty("service").GetString());
        var lobby = await (await host.GetAsync($"/api/lobbies/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Waiting", lobby.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Start_GameServiceFails_Returns502AndLobbyStaysWaiting()
    {
        var host = ClientFor(1571);
        var guest = ClientFor(LobbyApiFactory.GameServiceFailsForUserId);
        var id = await LobbyWith(host, [guest], allReady: true);

        var response = await host.PostAsync($"/api/lobbies/{id}/start", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(502, problem!.Status);
        var lobby = await (await host.GetAsync($"/api/lobbies/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Waiting", lobby.GetProperty("status").GetString());

        // Players are still in the lobby, so they cannot create another one.
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsJsonAsync("/api/lobbies", Body())).StatusCode);
    }

    [Fact]
    public async Task Start_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().PostAsync($"/api/lobbies/{Guid.NewGuid()}/start", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

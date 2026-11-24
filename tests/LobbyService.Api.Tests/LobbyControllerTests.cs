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

    [Fact]
    public async Task Health_IsAnonymous()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

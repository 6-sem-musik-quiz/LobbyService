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
    public async Task Health_IsAnonymous()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

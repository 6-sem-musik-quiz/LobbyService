using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LobbyService.Repository.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace LobbyService.Api.Tests;

// GET /api/lobbies/mine and the "stuck in a lobby" cases: a player who left the page can find their
// lobby again, and a reference to a lobby that is gone no longer blocks creating or joining.
public class MyLobbyTests(LobbyApiFactory factory) : IClassFixture<LobbyApiFactory>
{
    private HttpClient ClientFor(int userId, string userName)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", LobbyApiFactory.CreateToken(userId, userName));
        return client;
    }

    private static readonly object PublicLobby = new
    {
        categoryId = LobbyApiFactory.KnownPlaylistId, answerTimeSeconds = 30, rounds = 10, maxPlayers = 4, isPrivate = false
    };

    private static async Task<Guid> IdOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    [Fact]
    public async Task Mine_InALobby_Returns200WithIt()
    {
        var alice = ClientFor(5001, "alice");
        var lobbyId = await IdOf(await alice.PostAsJsonAsync("/api/lobbies", PublicLobby));

        var response = await alice.GetAsync("/api/lobbies/mine");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(lobbyId, await IdOf(response));
    }

    [Fact]
    public async Task Mine_NotInALobby_Returns404()
    {
        var response = await ClientFor(5002, "bob").GetAsync("/api/lobbies/mine");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Mine_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/lobbies/mine");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // What happened to Mathias: he joined, left the page without leaving, and the host left.
    // He is now the only player (and host) of a lobby he cannot see. /mine finds it, and he can leave it.
    [Fact]
    public async Task PlayerWhoLeftThePage_FindsTheLobbyAndCanLeaveAndCreateAgain()
    {
        var host = ClientFor(5011, "host");
        var mathias = ClientFor(5012, "mathias");
        var lobbyId = await IdOf(await host.PostAsJsonAsync("/api/lobbies", PublicLobby));
        await mathias.PostAsync($"/api/lobbies/{lobbyId}/join", null);
        await host.PostAsync($"/api/lobbies/{lobbyId}/leave", null);

        Assert.Equal(HttpStatusCode.Conflict, (await mathias.PostAsJsonAsync("/api/lobbies", PublicLobby)).StatusCode);

        var mine = await mathias.GetAsync("/api/lobbies/mine");
        Assert.Equal(lobbyId, await IdOf(mine));

        Assert.Equal(HttpStatusCode.NoContent, (await mathias.PostAsync($"/api/lobbies/{lobbyId}/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await mathias.PostAsJsonAsync("/api/lobbies", PublicLobby)).StatusCode);
    }

    // The lobby itself is gone but the user's reference survived: nothing should stay blocked.
    [Fact]
    public async Task LobbyGoneButReferenceLeft_DoesNotBlockCreateOrJoin()
    {
        var store = (LobbyApiFactory.InMemoryLobbyCacheRepository)factory.Services.GetRequiredService<ILobbyCacheRepository>();
        var carol = ClientFor(5021, "carol");
        var lostId = await IdOf(await carol.PostAsJsonAsync("/api/lobbies", PublicLobby));
        store.LoseLobby(lostId);

        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync("/api/lobbies/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await carol.PostAsJsonAsync("/api/lobbies", PublicLobby)).StatusCode);

        var dave = ClientFor(5022, "dave");
        var daveLost = await IdOf(await dave.PostAsJsonAsync("/api/lobbies", PublicLobby));
        store.LoseLobby(daveLost);
        var other = await IdOf(await ClientFor(5023, "erin").PostAsJsonAsync("/api/lobbies", PublicLobby));

        Assert.Equal(HttpStatusCode.OK, (await dave.PostAsync($"/api/lobbies/{other}/join", null)).StatusCode);
    }
}

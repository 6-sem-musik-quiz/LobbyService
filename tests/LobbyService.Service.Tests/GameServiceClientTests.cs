using System.Net;
using System.Text;
using System.Text.Json;
using LobbyService.Domain.Dtos;
using LobbyService.Domain.Exceptions;
using LobbyService.Service.Clients;

namespace LobbyService.Service.Tests;

// Checks the request LobbyService sends to GameService POST /api/games and how failures are reported.
public class GameServiceClientTests
{
    private static readonly Guid LobbyId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid GameId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static CreateGameRequest Request() =>
        new(LobbyId, 3, "Rock", 10, 30, [new GamePlayerDto(1, "host"), new GamePlayerDto(2, "guest")]);

    private static GameServiceClient ClientWith(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://gameservice/") });

    [Fact]
    public async Task CreateGameAsync_SendsContractBodyAndBearerToken_ReturnsGameId()
    {
        var handler = new StubHandler(HttpStatusCode.Created, $$"""{ "gameId": "{{GameId}}" }""");

        var gameId = await ClientWith(handler).CreateGameAsync(Request(), "host-token");

        Assert.Equal(GameId, gameId);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("http://gameservice/api/games", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("host-token", handler.Request.Headers.Authorization.Parameter);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal(LobbyId, body.GetProperty("lobbyId").GetGuid());
        Assert.Equal(3, body.GetProperty("categoryId").GetInt32());
        Assert.Equal("Rock", body.GetProperty("categoryName").GetString());
        Assert.Equal(10, body.GetProperty("rounds").GetInt32());
        Assert.Equal(30, body.GetProperty("answerTimeSeconds").GetInt32());
        var players = body.GetProperty("players").EnumerateArray().ToList();
        Assert.Equal([1, 2], players.Select(p => p.GetProperty("userId").GetInt32()));
        Assert.Equal(["host", "guest"], players.Select(p => p.GetProperty("userName").GetString()));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CreateGameAsync_ErrorStatus_ThrowsGameServiceUnavailable(HttpStatusCode status)
    {
        var handler = new StubHandler(status, "");

        await Assert.ThrowsAsync<GameServiceUnavailableException>(() =>
            ClientWith(handler).CreateGameAsync(Request(), "host-token"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public async Task CreateGameAsync_InvalidBody_ThrowsGameServiceUnavailable(string body)
    {
        var handler = new StubHandler(HttpStatusCode.OK, body);

        await Assert.ThrowsAsync<GameServiceUnavailableException>(() =>
            ClientWith(handler).CreateGameAsync(Request(), "host-token"));
    }

    [Fact]
    public async Task CreateGameAsync_NetworkError_ThrowsGameServiceUnavailable()
    {
        await Assert.ThrowsAsync<GameServiceUnavailableException>(() =>
            ClientWith(new ThrowingHandler()).CreateGameAsync(Request(), "host-token"));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }
}

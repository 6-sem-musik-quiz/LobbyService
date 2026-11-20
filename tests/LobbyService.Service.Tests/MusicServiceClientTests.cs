using System.Net;
using System.Text;
using LobbyService.Service.Clients;

namespace LobbyService.Service.Tests;

// Checks that the client reads the SCRUM-92 contract: GET /api/playlists/{id} → { id, name, songCount }.
public class MusicServiceClientTests
{
    [Fact]
    public async Task GetPlaylistAsync_ReadsContractJsonAndSendsBearerToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{ "id": 7, "name": "90'er hits", "songCount": 42 }""");
        var client = new MusicServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://musicservice/") });

        var playlist = await client.GetPlaylistAsync(7, "token-123");

        Assert.NotNull(playlist);
        Assert.Equal(7, playlist.Id);
        Assert.Equal("90'er hits", playlist.Name);
        Assert.Equal(42, playlist.SongCount);
        Assert.Equal("http://musicservice/api/playlists/7", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("token-123", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetPlaylistAsync_NotFound_ReturnsNull()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "");
        var client = new MusicServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://musicservice/") });

        Assert.Null(await client.GetPlaylistAsync(99, "token-123"));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}

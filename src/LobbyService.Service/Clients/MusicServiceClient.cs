using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LobbyService.Domain.Dtos;
using LobbyService.Service.Interfaces;

namespace LobbyService.Service.Clients;

// Typed HttpClient for MusicService. Base address is configured in AddServiceLayer.
public class MusicServiceClient(HttpClient httpClient) : IMusicServiceClient
{
    public async Task<PlaylistDto?> GetPlaylistAsync(int playlistId, string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/playlists/{playlistId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlaylistDto>(cancellationToken);
    }
}

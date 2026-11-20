using LobbyService.Domain.Dtos;

namespace LobbyService.Service.Interfaces;

public interface IMusicServiceClient
{
    // Calls MusicService GET /api/playlists/{id} with the user's token. Returns null on 404.
    Task<PlaylistDto?> GetPlaylistAsync(int playlistId, string accessToken, CancellationToken cancellationToken = default);
}

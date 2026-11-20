namespace LobbyService.Domain.Dtos;

// Response from MusicService GET /api/playlists/{id} (contract in SCRUM-92):
// 200 { id, name, songCount }, 404 if the playlist does not exist.
public record PlaylistDto(int Id, string Name, int SongCount);

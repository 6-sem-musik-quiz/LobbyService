namespace LobbyService.Domain.Dtos;

// Response from MusicService GET /api/playlists/{id} (SCRUM-92).
// Only the fields LobbyService needs. The MusicService contract is not yet published in
// Platform, so both an explicit songCount and a songs list are accepted, and song items
// are not interpreted (only counted).
public record PlaylistDto(
    string Name,
    int? SongCount = null,
    IReadOnlyList<object>? Songs = null)
{
    public int TotalSongs => SongCount ?? Songs?.Count ?? 0;
}

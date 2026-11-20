namespace LobbyService.Domain.Exceptions;

// Invalid input. Mapped to 400 by the API.
public class LobbyValidationException(string message) : Exception(message);

// The requested playlist does not exist in MusicService. Mapped to 400.
public class PlaylistNotFoundException(int playlistId)
    : LobbyValidationException($"Playlist {playlistId} does not exist.")
{
    public int PlaylistId { get; } = playlistId;
}

// Conflicts with the current state, e.g. the user is already in a lobby. Mapped to 409.
public class LobbyConflictException(string message) : Exception(message);

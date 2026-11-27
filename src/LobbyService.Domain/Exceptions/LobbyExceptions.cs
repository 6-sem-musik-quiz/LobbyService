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

// The lobby does not exist, or it is private and the user is not in it. Mapped to 404.
public class LobbyNotFoundException(Guid lobbyId) : Exception($"Lobby {lobbyId} was not found.")
{
    public Guid LobbyId { get; } = lobbyId;
}

// The user is in the lobby but may not do this, e.g. start the game without being host. Mapped to 403.
public class LobbyForbiddenException(string message) : Exception(message);

// GameService could not be reached or did not create the game. Mapped to 502.
public class GameServiceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

// SCRUM-179: a service the lobby depends on has been switched off by an admin ("design to be
// disabled"), e.g. GameService when the host starts a game. Mapped to 503 with the same ProblemDetails
// the edge gives (SCRUM-194), so the frontend shows its maintenance toast.
public class ServiceInMaintenanceException(string service, string message) : Exception(message)
{
    public const string ProblemType = "https://quiz.munkcloud.dk/problems/maintenance";
    public string Service { get; } = service;
}

// No lobby has the given join code. Mapped to 404.
public class LobbyCodeNotFoundException(string code) : Exception($"No lobby has the code '{code}'.")
{
    public string Code { get; } = code;
}

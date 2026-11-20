using LobbyService.Domain.Dtos;
using LobbyService.Domain.Entities;
using LobbyService.Domain.Exceptions;
using LobbyService.Repository.Interfaces;
using LobbyService.Service.Interfaces;

namespace LobbyService.Service.Services;

// Lobby rules: allowed settings, host, max players and playlist.
public class LobbyManager(
    ILobbyCacheRepository repository,
    IMusicServiceClient musicService,
    ILobbyCodeGenerator codeGenerator,
    TimeProvider timeProvider) : ILobbyManager
{
    public static readonly IReadOnlySet<int> AllowedAnswerTimes = new HashSet<int> { 15, 30, 45, 60 };
    public static readonly IReadOnlySet<int> AllowedRounds = new HashSet<int> { 5, 10, 15, 20 };
    public const int MinPlayers = 2;
    public const int MaxPlayers = 8;
    public const int MaxCodeAttempts = 10;

    public async Task<LobbyDto> CreateAsync(CreateLobbyRequest request, int userId, string userName, string accessToken,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        if (await repository.GetLobbyIdForUserAsync(userId) is not null)
            throw new LobbyConflictException("You are already in a lobby.");

        var playlist = await musicService.GetPlaylistAsync(request.CategoryId, accessToken, cancellationToken)
                       ?? throw new PlaylistNotFoundException(request.CategoryId);

        // One start card plus one song per round.
        var requiredSongs = request.Rounds + 1;
        if (playlist.TotalSongs < requiredSongs)
            throw new LobbyValidationException(
                $"The playlist has {playlist.TotalSongs} songs, but {request.Rounds} rounds need at least {requiredSongs}.");

        var lobby = new Lobby
        {
            Id = Guid.NewGuid(),
            HostUserId = userId,
            IsPrivate = request.IsPrivate,
            Settings = new LobbySettings
            {
                CategoryId = request.CategoryId,
                CategoryName = playlist.Name,
                AnswerTimeSeconds = request.AnswerTimeSeconds,
                Rounds = request.Rounds,
                MaxPlayers = request.MaxPlayers
            },
            Players = [new LobbyPlayer { UserId = userId, UserName = userName, IsReady = false }],
            Status = LobbyStatus.Waiting,
            CreatedAt = timeProvider.GetUtcNow()
        };

        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            lobby.Code = request.IsPrivate ? codeGenerator.Generate() : null;

            switch (await repository.TryCreateAsync(lobby))
            {
                case CreateLobbyResult.Created:
                    return LobbyDto.FromEntity(lobby);
                case CreateLobbyResult.UserAlreadyInLobby:
                    throw new LobbyConflictException("You are already in a lobby.");
                case CreateLobbyResult.CodeTaken when request.IsPrivate:
                    continue;
                default:
                    throw new InvalidOperationException("Unexpected result when storing the lobby.");
            }
        }

        throw new InvalidOperationException($"Could not generate a unique lobby code after {MaxCodeAttempts} attempts.");
    }

    private static void Validate(CreateLobbyRequest request)
    {
        if (!AllowedAnswerTimes.Contains(request.AnswerTimeSeconds))
            throw new LobbyValidationException("answerTimeSeconds must be one of 15, 30, 45 or 60.");
        if (!AllowedRounds.Contains(request.Rounds))
            throw new LobbyValidationException("rounds must be one of 5, 10, 15 or 20.");
        if (request.MaxPlayers is < MinPlayers or > MaxPlayers)
            throw new LobbyValidationException($"maxPlayers must be between {MinPlayers} and {MaxPlayers}.");
    }
}

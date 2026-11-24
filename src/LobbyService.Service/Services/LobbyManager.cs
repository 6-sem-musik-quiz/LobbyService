using LobbyService.Domain.Dtos;
using LobbyService.Domain.Entities;
using LobbyService.Domain.Exceptions;
using LobbyService.Repository.Interfaces;
using LobbyService.Service.Hubs;
using LobbyService.Service.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace LobbyService.Service.Services;

// Lobby rules: allowed settings, host, max players and playlist.
// After every successful write the clients in the lobby's SignalR group are notified.
public class LobbyManager(
    ILobbyCacheRepository repository,
    IMusicServiceClient musicService,
    IGameServiceClient gameService,
    ILobbyCodeGenerator codeGenerator,
    TimeProvider timeProvider,
    IHubContext<LobbyHub, ILobbyClient> hubContext,
    ILogger<LobbyManager> logger) : ILobbyManager
{
    public static readonly IReadOnlySet<int> AllowedAnswerTimes = new HashSet<int> { 15, 30, 45, 60 };
    public static readonly IReadOnlySet<int> AllowedRounds = new HashSet<int> { 5, 10, 15, 20 };
    public const int MinPlayers = 2;
    public const int MaxPlayers = 8;
    public const int MaxCodeAttempts = 10;
    public const int MaxUpdateAttempts = 5;

    public async Task<LobbyDto> CreateAsync(CreateLobbyRequest request, int userId, string userName, string accessToken,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        if (await GetCurrentLobbyAsync(userId) is not null)
            throw new LobbyConflictException("You are already in a lobby.");

        var playlist = await musicService.GetPlaylistAsync(request.CategoryId, accessToken, cancellationToken)
                       ?? throw new PlaylistNotFoundException(request.CategoryId);

        // One start card plus one song per round.
        var requiredSongs = request.Rounds + 1;
        if (playlist.SongCount < requiredSongs)
            throw new LobbyValidationException(
                $"The playlist has {playlist.SongCount} songs, but {request.Rounds} rounds need at least {requiredSongs}.");

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

    public async Task<LobbyDto?> GetMyLobbyAsync(int userId) =>
        await GetCurrentLobbyAsync(userId) is { } lobby ? LobbyDto.FromEntity(lobby) : null;

    // The user-lobby key is only trusted when its lobby still exists, is waiting and lists the user.
    // Anything else is a stale key (e.g. left over from an older version, or Redis lost the lobby)
    // and is deleted, so the user is not locked out of creating and joining lobbies.
    private async Task<Lobby?> GetCurrentLobbyAsync(int userId)
    {
        if (await repository.GetLobbyIdForUserAsync(userId) is not { } lobbyId)
            return null;

        var lobby = await repository.GetAsync(lobbyId);
        if (lobby is not null && lobby.Status == LobbyStatus.Waiting && lobby.Players.Any(p => p.UserId == userId))
            return lobby;

        logger.LogInformation("Clearing stale user-lobby key for user {UserId} (lobby {LobbyId}).", userId, lobbyId);
        await repository.ClearUserLobbyAsync(userId, lobbyId);
        return null;
    }

    public async Task<IReadOnlyList<LobbySummaryDto>> GetOpenLobbiesAsync()
    {
        var lobbies = await repository.GetPublicWaitingAsync();

        // The repository already filters, but the rule belongs here as well.
        return lobbies
            .Where(l => !l.IsPrivate && l.Status == LobbyStatus.Waiting)
            .OrderByDescending(l => l.CreatedAt)
            .Select(LobbySummaryDto.FromEntity)
            .ToList();
    }

    public async Task<LobbyDto> GetByIdAsync(Guid lobbyId, int userId)
    {
        var lobby = await repository.GetAsync(lobbyId);
        if (lobby is null || (lobby.IsPrivate && lobby.Players.All(p => p.UserId != userId)))
            throw new LobbyNotFoundException(lobbyId);

        return LobbyDto.FromEntity(lobby);
    }

    public Task<LobbyDto> JoinAsync(Guid lobbyId, int userId, string userName) =>
        JoinWithRetryAsync(userId, userName, async () =>
        {
            // Private lobbies can only be joined with their code.
            var lobby = await repository.GetAsync(lobbyId);
            return lobby is null || lobby.IsPrivate ? throw new LobbyNotFoundException(lobbyId) : lobby;
        });

    public async Task<LobbyDto> JoinByCodeAsync(string code, int userId, string userName)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            throw new LobbyCodeNotFoundException(normalized);

        var lobbyId = await repository.GetLobbyIdByCodeAsync(normalized)
                      ?? throw new LobbyCodeNotFoundException(normalized);

        return await JoinWithRetryAsync(userId, userName, async () =>
            await repository.GetAsync(lobbyId) ?? throw new LobbyCodeNotFoundException(normalized));
    }

    public async Task LeaveAsync(Guid lobbyId, int userId)
    {
        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var lobby = await repository.GetAsync(lobbyId);
            if (lobby is null || lobby.Players.All(p => p.UserId != userId))
                throw new LobbyNotFoundException(lobbyId);
            if (lobby.Status == LobbyStatus.InGame)
                throw new LobbyConflictException("You cannot leave a lobby while the game is running.");

            Lobby? after = null;
            if (lobby.Players.Count > 1)
            {
                after = Clone(lobby);
                after.Players.RemoveAll(p => p.UserId == userId);
                // Players are kept in join order, so the first remaining player has been there longest.
                if (after.HostUserId == userId)
                    after.HostUserId = after.Players[0].UserId;
            }

            if (await repository.TryLeaveAsync(lobby, after, userId) == LobbyUpdateResult.Updated)
            {
                if (after is null)
                    await NotifyAsync(lobbyId, client => client.LobbyClosed());
                else
                {
                    var dto = LobbyDto.FromEntity(after);
                    await NotifyAsync(lobbyId, client => client.LobbyUpdated(dto));
                }
                return;
            }
        }

        throw new LobbyConflictException("The lobby is busy. Please try again.");
    }

    public async Task<LobbyDto> SetReadyAsync(Guid lobbyId, int userId, bool ready)
    {
        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var lobby = await repository.GetAsync(lobbyId);
            var player = lobby?.Players.FirstOrDefault(p => p.UserId == userId);
            if (lobby is null || player is null)
                throw new LobbyNotFoundException(lobbyId);
            if (lobby.Status == LobbyStatus.InGame)
                throw new LobbyConflictException("The game has already started.");
            if (player.IsReady == ready)
                return LobbyDto.FromEntity(lobby);

            var after = Clone(lobby);
            after.Players.First(p => p.UserId == userId).IsReady = ready;

            if (await repository.TryUpdateAsync(lobby, after) == LobbyUpdateResult.Updated)
            {
                var dto = LobbyDto.FromEntity(after);
                await NotifyAsync(lobbyId, client => client.LobbyUpdated(dto));
                return dto;
            }
        }

        throw new LobbyConflictException("The lobby is busy. Please try again.");
    }

    public async Task<StartGameResponse> StartAsync(Guid lobbyId, int userId, string accessToken,
        CancellationToken cancellationToken = default)
    {
        // GameService is called once. If the lobby changes before it is marked InGame, the start is only
        // completed when the fresh lobby still has exactly the players the game was created with.
        Guid? gameId = null;
        int[] gamePlayers = [];

        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var lobby = await repository.GetAsync(lobbyId);
            if (lobby is null || lobby.Players.All(p => p.UserId != userId))
                throw new LobbyNotFoundException(lobbyId);
            EnsureCanStart(lobby, userId);

            var playerIds = lobby.Players.Select(p => p.UserId).ToArray();
            if (gameId is null)
            {
                // Throws GameServiceUnavailableException (502); nothing has been written, so the lobby stays Waiting.
                gameId = await gameService.CreateGameAsync(ToCreateGameRequest(lobby), accessToken, cancellationToken);
                gamePlayers = playerIds;
            }
            else if (!playerIds.SequenceEqual(gamePlayers))
            {
                throw new LobbyConflictException("The players changed while the game was starting. Please try again.");
            }

            var after = Clone(lobby);
            after.Status = LobbyStatus.InGame;
            after.GameId = gameId;

            if (await repository.TryStartAsync(lobby, after) == LobbyUpdateResult.Updated)
            {
                var response = new StartGameResponse(gameId.Value);
                await NotifyAsync(lobbyId, client => client.GameStarted(response));
                return response;
            }
        }

        throw new LobbyConflictException("The lobby is busy. Please try again.");
    }

    private static void EnsureCanStart(Lobby lobby, int userId)
    {
        if (lobby.HostUserId != userId)
            throw new LobbyForbiddenException("Only the host can start the game.");
        if (lobby.Status == LobbyStatus.InGame)
            throw new LobbyConflictException("The game has already started.");
        if (lobby.Players.Count < MinPlayers)
            throw new LobbyConflictException($"At least {MinPlayers} players are needed to start the game.");
        if (lobby.Players.Any(p => !p.IsReady))
            throw new LobbyConflictException("Not all players are ready.");
    }

    private static CreateGameRequest ToCreateGameRequest(Lobby lobby) => new(
        lobby.Id,
        lobby.Settings.CategoryId,
        lobby.Settings.CategoryName,
        lobby.Settings.Rounds,
        lobby.Settings.AnswerTimeSeconds,
        lobby.Players.Select(p => new GamePlayerDto(p.UserId, p.UserName)).ToList());

    // Reads the lobby, applies the join rules and writes it with compare-and-swap.
    // If another request changed the lobby in between, the whole thing is retried on fresh data.
    private async Task<LobbyDto> JoinWithRetryAsync(int userId, string userName, Func<Task<Lobby>> loadLobby)
    {
        for (var attempt = 0; attempt < MaxUpdateAttempts; attempt++)
        {
            var lobby = await loadLobby();

            if (lobby.Players.Any(p => p.UserId == userId))
                return LobbyDto.FromEntity(lobby);
            if (lobby.Status == LobbyStatus.InGame)
                throw new LobbyConflictException("The game has already started.");
            if (lobby.Players.Count >= lobby.Settings.MaxPlayers)
                throw new LobbyConflictException("The lobby is full.");
            if (await GetCurrentLobbyAsync(userId) is not null)
                throw new LobbyConflictException("You are already in another lobby.");

            var after = Clone(lobby);
            after.Players.Add(new LobbyPlayer { UserId = userId, UserName = userName, IsReady = false });

            switch (await repository.TryJoinAsync(lobby, after, userId))
            {
                case LobbyUpdateResult.Updated:
                    var dto = LobbyDto.FromEntity(after);
                    await NotifyAsync(after.Id, client => client.LobbyUpdated(dto));
                    return dto;
                case LobbyUpdateResult.UserInOtherLobby:
                    throw new LobbyConflictException("You are already in another lobby.");
                case LobbyUpdateResult.Stale:
                    continue;
            }
        }

        throw new LobbyConflictException("The lobby is busy. Please try again.");
    }

    // The change is already stored when this runs, so a failed push is logged instead of failing the request.
    // Clients that miss a message still get the current state from GET /api/lobbies/{id}.
    private async Task NotifyAsync(Guid lobbyId, Func<ILobbyClient, Task> send)
    {
        try
        {
            await send(hubContext.Clients.Group(LobbyHub.GroupName(lobbyId)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not send the SignalR update for lobby {LobbyId}.", lobbyId);
        }
    }

    // Copy used as the new state, so the original can still be compared with what is stored.
    private static Lobby Clone(Lobby lobby) => new()
    {
        Id = lobby.Id,
        Code = lobby.Code,
        HostUserId = lobby.HostUserId,
        IsPrivate = lobby.IsPrivate,
        Settings = new LobbySettings
        {
            CategoryId = lobby.Settings.CategoryId,
            CategoryName = lobby.Settings.CategoryName,
            AnswerTimeSeconds = lobby.Settings.AnswerTimeSeconds,
            Rounds = lobby.Settings.Rounds,
            MaxPlayers = lobby.Settings.MaxPlayers
        },
        Players = lobby.Players
            .Select(p => new LobbyPlayer { UserId = p.UserId, UserName = p.UserName, IsReady = p.IsReady })
            .ToList(),
        Status = lobby.Status,
        GameId = lobby.GameId,
        CreatedAt = lobby.CreatedAt
    };

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

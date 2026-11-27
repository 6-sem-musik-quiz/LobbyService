using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LobbyService.Domain.Dtos;
using LobbyService.Domain.Entities;
using LobbyService.Domain.Exceptions;
using LobbyService.Repository.Interfaces;
using LobbyService.Repository.Metrics;
using LobbyService.Service.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LobbyService.Api.Tests;

// Runs the API with an in-memory lobby store, a fake MusicService and a test signing key.
// By default SignalR uses the in-memory (single-pod) lifetime manager, so the tests need no Redis.
// The two-pod backplane tests (SCRUM-128) pass a shared store, so two factories act as two pods of
// one deployment, and optionally a Redis connection string to turn the real Redis backplane on.
public class LobbyApiFactory : WebApplicationFactory<Program>
{
    // Overridden by the two-pod tests (BackplaneTests.Pod). xUnit fixtures need a single public constructor.
    protected virtual ILobbyCacheRepository? SharedRepository => null;
    protected virtual string? BackplaneRedis => null;
    protected virtual void ConfigureRabbitMq(IWebHostBuilder builder) => builder.UseSetting("RabbitMQ:Enabled", "false");

    // Test-only key, not a secret.
    public const string SigningKey = "test-signing-key-for-lobbyservice-api-tests-0123456789";
    public const int KnownPlaylistId = 1;
    public const int UnknownPlaylistId = 404;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        // No RabbitMQ in the tests; GameFinished is handed to GameFinishedHandler directly (PlayAgainTests).
        // RabbitMqPlayAgainTests overrides ConfigureRabbitMq to use a real broker.
        ConfigureRabbitMq(builder);
        var redis = BackplaneRedis;
        var sharedRepository = SharedRepository;
        if (redis is not null)
            builder.UseSetting("ConnectionStrings:Redis", redis);
        builder.ConfigureTestServices(services =>
        {
            // Last registration wins: replaces the Redis backplane from Program.cs with the
            // in-memory one, which only reaches connections on this "pod".
            if (redis is null)
                services.AddSingleton(typeof(HubLifetimeManager<>), typeof(DefaultHubLifetimeManager<>));
            services.RemoveAll<ILobbyCacheRepository>();
            if (sharedRepository is null)
                services.AddSingleton<ILobbyCacheRepository, InMemoryLobbyCacheRepository>();
            else
                services.AddSingleton(sharedRepository);
            services.RemoveAll<IMusicServiceClient>();
            services.AddSingleton<IMusicServiceClient, FakeMusicServiceClient>();
            services.RemoveAll<IGameServiceClient>();
            services.AddSingleton<IGameServiceClient, FakeGameServiceClient>();
        });
    }

    // The fake GameService fails whenever this user is one of the players.
    public const int GameServiceFailsForUserId = 1999;
    // SCRUM-179: a lobby with this player gets the maintenance answer from (fake) GameService.
    public const int GameServiceInMaintenanceForUserId = 4242;

    public static string CreateToken(int userId, string userName, string signingKey = SigningKey)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "slopster-userservice",
            Audience = "slopster",
            Subject = new ClaimsIdentity([new Claim("sub", userId.ToString()), new Claim("unique_name", userName)]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private class FakeMusicServiceClient : IMusicServiceClient
    {
        public Task<PlaylistDto?> GetPlaylistAsync(int playlistId, string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(playlistId == KnownPlaylistId ? new PlaylistDto(KnownPlaylistId, "Test playlist", 30) : null);
    }

    private class FakeGameServiceClient : IGameServiceClient
    {
        public Task<Guid> CreateGameAsync(CreateGameRequest request, string accessToken, CancellationToken cancellationToken = default) =>
            request.Players.Any(p => p.UserId == GameServiceInMaintenanceForUserId)
                ? throw new ServiceInMaintenanceException("gameservice", "Back at 20:00")
                : request.Players.Any(p => p.UserId == GameServiceFailsForUserId) || string.IsNullOrEmpty(accessToken)
                    ? throw new GameServiceUnavailableException("GameService returned 503 when creating the game.")
                    : Task.FromResult(Guid.NewGuid());
    }

    // Mirrors the Redis repository: lobbies are stored as JSON and updates are compare-and-swap under a lock.
    public class InMemoryLobbyCacheRepository : ILobbyCacheRepository
    {
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<Guid, string> _lobbies = new();
        private readonly ConcurrentDictionary<string, Guid> _codes = new();
        private readonly ConcurrentDictionary<int, Guid> _userLobbies = new();
        private readonly ConcurrentDictionary<Guid, byte> _publicLobbies = new();

        private static string Serialize(Lobby lobby) => JsonSerializer.Serialize(lobby);
        private static Lobby Deserialize(string json) => JsonSerializer.Deserialize<Lobby>(json)!;

        public Task<Lobby?> GetAsync(Guid lobbyId) =>
            Task.FromResult(_lobbies.TryGetValue(lobbyId, out var json) ? Deserialize(json) : null);

        public Task<Guid?> GetLobbyIdForUserAsync(int userId) =>
            Task.FromResult(_userLobbies.TryGetValue(userId, out var id) ? id : (Guid?)null);

        public Task ClearUserLobbyAsync(int userId, Guid lobbyId)
        {
            lock (_lock)
            {
                if (_userLobbies.TryGetValue(userId, out var current) && current == lobbyId)
                    _userLobbies.TryRemove(userId, out _);
                return Task.CompletedTask;
            }
        }

        // Test hook: makes a lobby disappear without touching the players' user-lobby keys,
        // like a lobby key that expired or was lost while the user-lobby key survived.
        public void LoseLobby(Guid lobbyId)
        {
            lock (_lock)
            {
                _lobbies.TryRemove(lobbyId, out _);
                _publicLobbies.TryRemove(lobbyId, out _);
            }
        }

        public Task<Guid?> GetLobbyIdByCodeAsync(string code) =>
            Task.FromResult(_codes.TryGetValue(code, out var id) ? id : (Guid?)null);

        public Task<CreateLobbyResult> TryCreateAsync(Lobby lobby)
        {
            lock (_lock)
            {
                if (_userLobbies.ContainsKey(lobby.HostUserId))
                    return Task.FromResult(CreateLobbyResult.UserAlreadyInLobby);
                if (lobby.Code is not null && _codes.ContainsKey(lobby.Code))
                    return Task.FromResult(CreateLobbyResult.CodeTaken);
                _userLobbies[lobby.HostUserId] = lobby.Id;
                if (lobby.Code is not null)
                    _codes[lobby.Code] = lobby.Id;
                _lobbies[lobby.Id] = Serialize(lobby);
                if (!lobby.IsPrivate && lobby.Status == LobbyStatus.Waiting)
                    _publicLobbies.TryAdd(lobby.Id, 0);
                return Task.FromResult(CreateLobbyResult.Created);
            }
        }

        public Task<LobbyUpdateResult> TryJoinAsync(Lobby before, Lobby after, int userId)
        {
            lock (_lock)
            {
                if (!IsUnchanged(before))
                    return Task.FromResult(LobbyUpdateResult.Stale);
                if (_userLobbies.ContainsKey(userId))
                    return Task.FromResult(LobbyUpdateResult.UserInOtherLobby);
                _userLobbies[userId] = after.Id;
                _lobbies[after.Id] = Serialize(after);
                if (!after.IsPrivate && after.Status == LobbyStatus.Waiting)
                    _publicLobbies.TryAdd(after.Id, 0);
                return Task.FromResult(LobbyUpdateResult.Updated);
            }
        }

        public Task<LobbyUpdateResult> TryLeaveAsync(Lobby before, Lobby? after, int userId)
        {
            lock (_lock)
            {
                if (!IsUnchanged(before))
                    return Task.FromResult(LobbyUpdateResult.Stale);
                _userLobbies.TryRemove(userId, out _);
                if (after is null)
                {
                    _lobbies.TryRemove(before.Id, out _);
                    if (before.Code is not null)
                        _codes.TryRemove(before.Code, out _);
                    _publicLobbies.TryRemove(before.Id, out _);
                }
                else
                {
                    _lobbies[after.Id] = Serialize(after);
                }
                return Task.FromResult(LobbyUpdateResult.Updated);
            }
        }

        public Task<LobbyUpdateResult> TryUpdateAsync(Lobby before, Lobby after)
        {
            lock (_lock)
            {
                if (!IsUnchanged(before))
                    return Task.FromResult(LobbyUpdateResult.Stale);
                _lobbies[after.Id] = Serialize(after);
                return Task.FromResult(LobbyUpdateResult.Updated);
            }
        }

        public Task<LobbyUpdateResult> TryStartAsync(Lobby before, Lobby after)
        {
            lock (_lock)
            {
                if (!IsUnchanged(before))
                    return Task.FromResult(LobbyUpdateResult.Stale);
                _lobbies[after.Id] = Serialize(after);
                _publicLobbies.TryRemove(after.Id, out _);
                foreach (var player in before.Players)
                    _userLobbies.TryRemove(player.UserId, out _);
                return Task.FromResult(LobbyUpdateResult.Updated);
            }
        }

        // SCRUM-172: like Redis, no user-lobby keys (nobody is back yet) and not listed until someone is.
        public Task<LobbyUpdateResult> TryReopenAsync(Lobby before, Lobby after)
        {
            lock (_lock)
            {
                if (!IsUnchanged(before))
                    return Task.FromResult(LobbyUpdateResult.Stale);
                _lobbies[after.Id] = Serialize(after);
                return Task.FromResult(LobbyUpdateResult.Updated);
            }
        }

        private bool IsUnchanged(Lobby before) =>
            _lobbies.TryGetValue(before.Id, out var json) && json == Serialize(before);

        public Task<IReadOnlyList<Lobby>> GetPublicWaitingAsync()
        {
            IReadOnlyList<Lobby> result = _publicLobbies.Keys
                .Select(id => _lobbies.TryGetValue(id, out var json) ? Deserialize(json) : null)
                .OfType<Lobby>()
                .Where(l => !l.IsPrivate && l.Status == LobbyStatus.Waiting && l.Players.Count > 0)
                .ToList();
            return Task.FromResult(result);
        }

        public Task<LobbyCounts> CountByStatusAsync()
        {
            var lobbies = _lobbies.Values.Select(Deserialize).ToList();
            return Task.FromResult(new LobbyCounts(
                lobbies.Count(l => l.Status == LobbyStatus.Waiting),
                lobbies.Count(l => l.Status == LobbyStatus.InGame)));
        }

        public Task RemoveFromPublicAsync(Guid lobbyId)
        {
            _publicLobbies.TryRemove(lobbyId, out _);
            return Task.CompletedTask;
        }
    }
}

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LobbyService.Domain.Dtos;
using LobbyService.Domain.Entities;
using LobbyService.Repository.Interfaces;
using LobbyService.Service.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LobbyService.Api.Tests;

// Runs the API with an in-memory lobby store, a fake MusicService and a test signing key.
public class LobbyApiFactory : WebApplicationFactory<Program>
{
    // Test-only key, not a secret.
    public const string SigningKey = "test-signing-key-for-lobbyservice-api-tests-0123456789";
    public const int KnownPlaylistId = 1;
    public const int UnknownPlaylistId = 404;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILobbyCacheRepository>();
            services.AddSingleton<ILobbyCacheRepository, InMemoryLobbyCacheRepository>();
            services.RemoveAll<IMusicServiceClient>();
            services.AddSingleton<IMusicServiceClient, FakeMusicServiceClient>();
        });
    }

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

    // Mirrors the Redis repository: lobbies are stored as JSON and updates are compare-and-swap under a lock.
    private class InMemoryLobbyCacheRepository : ILobbyCacheRepository
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

        private bool IsUnchanged(Lobby before) =>
            _lobbies.TryGetValue(before.Id, out var json) && json == Serialize(before);

        public Task<IReadOnlyList<Lobby>> GetPublicWaitingAsync()
        {
            IReadOnlyList<Lobby> result = _publicLobbies.Keys
                .Select(id => _lobbies.TryGetValue(id, out var json) ? Deserialize(json) : null)
                .OfType<Lobby>()
                .Where(l => !l.IsPrivate && l.Status == LobbyStatus.Waiting)
                .ToList();
            return Task.FromResult(result);
        }

        public Task RemoveFromPublicAsync(Guid lobbyId)
        {
            _publicLobbies.TryRemove(lobbyId, out _);
            return Task.CompletedTask;
        }
    }
}

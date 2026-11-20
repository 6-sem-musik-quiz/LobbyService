using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
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
            Task.FromResult(playlistId == KnownPlaylistId ? new PlaylistDto("Test playlist", SongCount: 30) : null);
    }

    private class InMemoryLobbyCacheRepository : ILobbyCacheRepository
    {
        private readonly ConcurrentDictionary<Guid, Lobby> _lobbies = new();
        private readonly ConcurrentDictionary<int, Guid> _userLobbies = new();

        public Task<Lobby?> GetAsync(Guid lobbyId) =>
            Task.FromResult(_lobbies.TryGetValue(lobbyId, out var lobby) ? lobby : null);

        public Task<Guid?> GetLobbyIdForUserAsync(int userId) =>
            Task.FromResult(_userLobbies.TryGetValue(userId, out var id) ? id : (Guid?)null);

        public Task<CreateLobbyResult> TryCreateAsync(Lobby lobby)
        {
            if (!_userLobbies.TryAdd(lobby.HostUserId, lobby.Id))
                return Task.FromResult(CreateLobbyResult.UserAlreadyInLobby);
            _lobbies[lobby.Id] = lobby;
            return Task.FromResult(CreateLobbyResult.Created);
        }
    }
}

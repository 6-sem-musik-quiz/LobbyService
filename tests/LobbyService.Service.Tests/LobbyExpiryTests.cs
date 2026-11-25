using LobbyService.Domain.Entities;
using LobbyService.Repository.Metrics;
using LobbyService.Repository.Repositories;
using NSubstitute;
using StackExchange.Redis;

namespace LobbyService.Service.Tests;

// SCRUM-160: a waiting lobby is removed from Redis after IdleTimeout (default 10 minutes) without
// activity. Redis expires the keys; these tests check that every write sets that TTL on all of them,
// so activity resets the timer and nothing outlives the lobby.
public class LobbyExpiryTests
{
    private readonly ITransaction _tran = Substitute.For<ITransaction>();

    private LobbyCacheRepository Repository(LobbyCacheSettings? settings = null)
    {
        var db = Substitute.For<IDatabase>();
        db.CreateTransaction(Arg.Any<object>()).Returns(_tran);
        _tran.ExecuteAsync(Arg.Any<CommandFlags>()).Returns(true);
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        return new LobbyCacheRepository(redis, new LobbyMetrics(), settings);
    }

    private static Lobby PrivateLobby(params int[] players) => new()
    {
        Id = Guid.NewGuid(),
        Code = "ABCDE",
        IsPrivate = true,
        HostUserId = players[0],
        Players = players.Select(id => new LobbyPlayer { UserId = id, UserName = $"p{id}" }).ToList()
    };

    // Key -> the TTL the transaction gave it (SET ... EX, or EXPIRE; the last one wins, as in Redis).
    private Dictionary<string, TimeSpan> Ttls() =>
        _tran.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name is "StringSetAsync" or "KeyExpireAsync")
            .Select(c => c.GetArguments())
            .Where(a => a[0] is RedisKey && a.OfType<TimeSpan>().Any())
            .GroupBy(a => ((RedisKey)a[0]!).ToString())
            .ToDictionary(g => g.Key, g => g.Last().OfType<TimeSpan>().First());

    [Fact]
    public void TheDefaultIdleTimeout_IsTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), LobbyCacheSettings.Default.IdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), LobbyCacheSettings.FromMinutes(null).IdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), LobbyCacheSettings.FromMinutes(0).IdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), LobbyCacheSettings.FromMinutes(3).IdleTimeout);
    }

    [Fact]
    public async Task ACreatedLobby_ExpiresAfterTenMinutes_WithItsCodeAndHost()
    {
        var lobby = PrivateLobby(1);

        await Repository().TryCreateAsync(lobby);

        var ten = TimeSpan.FromMinutes(10);
        Assert.Equal(new Dictionary<string, TimeSpan>
        {
            [LobbyCacheRepository.LobbyKey(lobby.Id)] = ten,
            [LobbyCacheRepository.CodeKey("ABCDE")] = ten,
            [LobbyCacheRepository.UserLobbyKey(1)] = ten
        }, Ttls());
    }

    [Fact]
    public async Task Activity_RenewsTheTimeout_OnEveryKey()
    {
        var before = PrivateLobby(1, 2);
        var after = PrivateLobby(1, 2);
        after.Id = before.Id;
        after.Players[1].IsReady = true;

        await Repository().TryUpdateAsync(before, after); // e.g. a player is ready

        var ten = TimeSpan.FromMinutes(10);
        Assert.Equal(new Dictionary<string, TimeSpan>
        {
            [LobbyCacheRepository.LobbyKey(before.Id)] = ten,
            [LobbyCacheRepository.CodeKey("ABCDE")] = ten,
            [LobbyCacheRepository.UserLobbyKey(1)] = ten,
            [LobbyCacheRepository.UserLobbyKey(2)] = ten
        }, Ttls());
    }

    [Fact]
    public async Task AJoin_RenewsTheTimeout_AndGivesTheNewPlayerTheSame()
    {
        var before = PrivateLobby(1);
        var after = PrivateLobby(1, 2);
        after.Id = before.Id;

        await Repository().TryJoinAsync(before, after, 2);

        Assert.All(Ttls().Values, ttl => Assert.Equal(TimeSpan.FromMinutes(10), ttl));
        Assert.Contains(LobbyCacheRepository.UserLobbyKey(2), Ttls().Keys);
        Assert.Contains(LobbyCacheRepository.LobbyKey(before.Id), Ttls().Keys);
    }

    [Fact]
    public async Task TheTimeout_IsConfigurable()
    {
        var lobby = PrivateLobby(1);

        await Repository(LobbyCacheSettings.FromMinutes(3)).TryCreateAsync(lobby);

        Assert.All(Ttls().Values, ttl => Assert.Equal(TimeSpan.FromMinutes(3), ttl));
    }

    [Fact]
    public async Task AStartedLobby_KeepsItsOwnTenMinutesAfterTheStart()
    {
        var before = PrivateLobby(1);
        var after = PrivateLobby(1);
        after.Id = before.Id;
        after.Status = LobbyStatus.InGame;

        await Repository(LobbyCacheSettings.FromMinutes(3)).TryStartAsync(before, after);

        Assert.Equal(LobbyCacheRepository.StartedLobbyTtl, Ttls()[LobbyCacheRepository.LobbyKey(before.Id)]);
    }
}

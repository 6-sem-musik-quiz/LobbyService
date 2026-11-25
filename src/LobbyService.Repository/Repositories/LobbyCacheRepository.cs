using System.Text.Json;
using System.Text.Json.Serialization;
using LobbyService.Domain.Entities;
using LobbyService.Repository.Interfaces;
using LobbyService.Repository.Metrics;
using StackExchange.Redis;

namespace LobbyService.Repository.Repositories;

// Keys:
//   lobby:{id}          -> lobby as JSON
//   lobbycode:{code}    -> lobby id (private lobbies only)
//   user-lobby:{userId} -> lobby id
//   lobbies:public      -> set of ids of public waiting lobbies (so listing needs no SCAN)
//   lobbies:waiting     -> sorted set of waiting lobby ids, score = expiry (unix seconds); for metrics only
//   lobbies:ingame      -> same for started lobbies; counted until their key expires
// SCRUM-160: waiting lobbies expire after IdleTimeout (default 10 minutes, Lobby:IdleTimeoutMinutes)
// without activity; every write (create, join, leave, ready, settings) renews the TTL.
// A started lobby expires 10 minutes after the start, and its code and user-lobby keys are deleted.
// The set has no TTL; ids of expired lobbies are pruned when the list is read.
public class LobbyCacheRepository(IConnectionMultiplexer redis, LobbyMetrics metrics, LobbyCacheSettings? settings = null)
    : ILobbyCacheRepository
{
    private readonly TimeSpan WaitingLobbyTtl = (settings ?? LobbyCacheSettings.Default).IdleTimeout;
    public static readonly TimeSpan StartedLobbyTtl = TimeSpan.FromMinutes(10);
    public const string PublicLobbiesKey = "lobbies:public";
    public const string WaitingLobbiesKey = "lobbies:waiting";
    public const string InGameLobbiesKey = "lobbies:ingame";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string LobbyKey(Guid lobbyId) => $"lobby:{lobbyId}";
    public static string CodeKey(string code) => $"lobbycode:{code}";
    public static string UserLobbyKey(int userId) => $"user-lobby:{userId}";

    private IDatabase Db => redis.GetDatabase();

    public async Task<Lobby?> GetAsync(Guid lobbyId)
    {
        var json = await Db.StringGetAsync(LobbyKey(lobbyId));
        metrics.RecordLookup(!json.IsNullOrEmpty);
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Lobby>(json.ToString(), JsonOptions);
    }

    public async Task<Guid?> GetLobbyIdForUserAsync(int userId)
    {
        var value = await Db.StringGetAsync(UserLobbyKey(userId));
        metrics.RecordLookup(!value.IsNullOrEmpty);
        return Guid.TryParse(value.ToString(), out var id) ? id : null;
    }

    public async Task ClearUserLobbyAsync(int userId, Guid lobbyId)
    {
        var tran = Db.CreateTransaction();
        tran.AddCondition(Condition.StringEqual(UserLobbyKey(userId), lobbyId.ToString()));
        _ = tran.KeyDeleteAsync(UserLobbyKey(userId));
        await tran.ExecuteAsync(); // false = the key changed in between; then there is nothing to clean
    }

    public async Task<CreateLobbyResult> TryCreateAsync(Lobby lobby)
    {
        var id = lobby.Id.ToString();
        var tran = Db.CreateTransaction();

        var userFree = tran.AddCondition(Condition.KeyNotExists(UserLobbyKey(lobby.HostUserId)));
        ConditionResult? codeFree = null;
        if (lobby.IsPrivate && lobby.Code is not null)
        {
            codeFree = tran.AddCondition(Condition.KeyNotExists(CodeKey(lobby.Code)));
            _ = tran.StringSetAsync(CodeKey(lobby.Code), id, WaitingLobbyTtl);
        }

        _ = tran.StringSetAsync(LobbyKey(lobby.Id), JsonSerializer.Serialize(lobby, JsonOptions), WaitingLobbyTtl);
        _ = tran.StringSetAsync(UserLobbyKey(lobby.HostUserId), id, WaitingLobbyTtl);
        _ = tran.SortedSetAddAsync(WaitingLobbiesKey, id, ExpiresAt(WaitingLobbyTtl));
        if (IsPublicWaiting(lobby))
            _ = tran.SetAddAsync(PublicLobbiesKey, id);

        if (await tran.ExecuteAsync())
            return CreateLobbyResult.Created;

        return !userFree.WasSatisfied ? CreateLobbyResult.UserAlreadyInLobby
            : codeFree is { WasSatisfied: false } ? CreateLobbyResult.CodeTaken
            : throw new InvalidOperationException("Redis transaction for lobby creation was aborted.");
    }

    public async Task<Guid?> GetLobbyIdByCodeAsync(string code)
    {
        var value = await Db.StringGetAsync(CodeKey(code));
        metrics.RecordLookup(!value.IsNullOrEmpty);
        return Guid.TryParse(value.ToString(), out var id) ? id : null;
    }

    // `before` is re-serialised with the same options it was stored with, so an unchanged lobby
    // produces the same JSON. Any concurrent write makes the condition fail (optimistic locking).
    public async Task<LobbyUpdateResult> TryJoinAsync(Lobby before, Lobby after, int userId)
    {
        var tran = Db.CreateTransaction();
        var unchanged = tran.AddCondition(Condition.StringEqual(LobbyKey(before.Id), Serialize(before)));
        var userFree = tran.AddCondition(Condition.KeyNotExists(UserLobbyKey(userId)));

        _ = tran.StringSetAsync(UserLobbyKey(userId), after.Id.ToString(), WaitingLobbyTtl);
        WriteLobby(tran, after);

        if (await tran.ExecuteAsync())
            return LobbyUpdateResult.Updated;

        return !unchanged.WasSatisfied ? LobbyUpdateResult.Stale
            : !userFree.WasSatisfied ? LobbyUpdateResult.UserInOtherLobby
            : throw new InvalidOperationException("Redis transaction for joining a lobby was aborted.");
    }

    public async Task<LobbyUpdateResult> TryLeaveAsync(Lobby before, Lobby? after, int userId)
    {
        var tran = Db.CreateTransaction();
        tran.AddCondition(Condition.StringEqual(LobbyKey(before.Id), Serialize(before)));

        _ = tran.KeyDeleteAsync(UserLobbyKey(userId));
        if (after is null)
        {
            _ = tran.KeyDeleteAsync(LobbyKey(before.Id));
            if (before.Code is not null)
                _ = tran.KeyDeleteAsync(CodeKey(before.Code));
            _ = tran.SetRemoveAsync(PublicLobbiesKey, before.Id.ToString());
            _ = tran.SortedSetRemoveAsync(WaitingLobbiesKey, before.Id.ToString());
        }
        else
        {
            WriteLobby(tran, after);
        }

        return await tran.ExecuteAsync() ? LobbyUpdateResult.Updated : LobbyUpdateResult.Stale;
    }

    public async Task<LobbyUpdateResult> TryUpdateAsync(Lobby before, Lobby after)
    {
        var tran = Db.CreateTransaction();
        tran.AddCondition(Condition.StringEqual(LobbyKey(before.Id), Serialize(before)));
        WriteLobby(tran, after);

        return await tran.ExecuteAsync() ? LobbyUpdateResult.Updated : LobbyUpdateResult.Stale;
    }

    public async Task<LobbyUpdateResult> TryStartAsync(Lobby before, Lobby after)
    {
        var tran = Db.CreateTransaction();
        tran.AddCondition(Condition.StringEqual(LobbyKey(before.Id), Serialize(before)));

        _ = tran.StringSetAsync(LobbyKey(after.Id), Serialize(after), StartedLobbyTtl);
        _ = tran.SetRemoveAsync(PublicLobbiesKey, after.Id.ToString());
        _ = tran.SortedSetRemoveAsync(WaitingLobbiesKey, after.Id.ToString());
        _ = tran.SortedSetAddAsync(InGameLobbiesKey, after.Id.ToString(), ExpiresAt(StartedLobbyTtl));
        if (before.Code is not null)
            _ = tran.KeyDeleteAsync(CodeKey(before.Code));
        foreach (var player in before.Players)
            _ = tran.KeyDeleteAsync(UserLobbyKey(player.UserId));

        return await tran.ExecuteAsync() ? LobbyUpdateResult.Updated : LobbyUpdateResult.Stale;
    }

    // Stores the lobby and renews the TTL of its code and of every player's user-lobby key.
    private void WriteLobby(ITransaction tran, Lobby lobby)
    {
        _ = tran.StringSetAsync(LobbyKey(lobby.Id), Serialize(lobby), WaitingLobbyTtl);
        _ = tran.SortedSetAddAsync(WaitingLobbiesKey, lobby.Id.ToString(), ExpiresAt(WaitingLobbyTtl));
        if (lobby.Code is not null)
            _ = tran.KeyExpireAsync(CodeKey(lobby.Code), WaitingLobbyTtl);
        foreach (var player in lobby.Players)
            _ = tran.KeyExpireAsync(UserLobbyKey(player.UserId), WaitingLobbyTtl);
    }

    private static double ExpiresAt(TimeSpan ttl) => DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();

    // Drops expired ids from both sorted sets, then counts what is left. No SCAN.
    public async Task<LobbyCounts> CountByStatusAsync()
    {
        var db = Db;
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await Task.WhenAll(
            db.SortedSetRemoveRangeByScoreAsync(WaitingLobbiesKey, double.NegativeInfinity, now),
            db.SortedSetRemoveRangeByScoreAsync(InGameLobbiesKey, double.NegativeInfinity, now));
        var waiting = db.SortedSetLengthAsync(WaitingLobbiesKey);
        var inGame = db.SortedSetLengthAsync(InGameLobbiesKey);
        return new LobbyCounts(await waiting, await inGame);
    }

    private static string Serialize(Lobby lobby) => JsonSerializer.Serialize(lobby, JsonOptions);

    public async Task<IReadOnlyList<Lobby>> GetPublicWaitingAsync()
    {
        var db = Db;
        var members = await db.SetMembersAsync(PublicLobbiesKey);
        if (members.Length == 0)
            return [];

        var keys = members.Select(m => (RedisKey)$"lobby:{m}").ToArray();
        var values = await db.StringGetAsync(keys); // MGET

        var lobbies = new List<Lobby>(members.Length);
        var stale = new List<RedisValue>();
        for (var i = 0; i < members.Length; i++)
        {
            var lobby = values[i].IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<Lobby>(values[i].ToString(), JsonOptions);
            if (lobby is not null && IsPublicWaiting(lobby))
                lobbies.Add(lobby);
            else
                stale.Add(members[i]);
        }

        if (stale.Count > 0)
            await db.SetRemoveAsync(PublicLobbiesKey, stale.ToArray());

        return lobbies;
    }

    public Task RemoveFromPublicAsync(Guid lobbyId) =>
        Db.SetRemoveAsync(PublicLobbiesKey, lobbyId.ToString());

    private static bool IsPublicWaiting(Lobby lobby) => !lobby.IsPrivate && lobby.Status == LobbyStatus.Waiting;
}

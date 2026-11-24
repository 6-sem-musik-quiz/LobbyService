using System.Text.Json;
using System.Text.Json.Serialization;
using LobbyService.Domain.Entities;
using LobbyService.Repository.Interfaces;
using StackExchange.Redis;

namespace LobbyService.Repository.Repositories;

// Keys:
//   lobby:{id}          -> lobby as JSON
//   lobbycode:{code}    -> lobby id (private lobbies only)
//   user-lobby:{userId} -> lobby id
//   lobbies:public      -> set of ids of public waiting lobbies (so listing needs no SCAN)
// Waiting lobbies expire after 2 hours without changes; every write renews the TTL.
// The set has no TTL; ids of expired lobbies are pruned when the list is read.
public class LobbyCacheRepository(IConnectionMultiplexer redis) : ILobbyCacheRepository
{
    public static readonly TimeSpan WaitingLobbyTtl = TimeSpan.FromHours(2);
    public const string PublicLobbiesKey = "lobbies:public";

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
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Lobby>(json.ToString(), JsonOptions);
    }

    public async Task<Guid?> GetLobbyIdForUserAsync(int userId)
    {
        var value = await Db.StringGetAsync(UserLobbyKey(userId));
        return Guid.TryParse(value.ToString(), out var id) ? id : null;
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
        if (IsPublicWaiting(lobby))
            _ = tran.SetAddAsync(PublicLobbiesKey, id);

        if (await tran.ExecuteAsync())
            return CreateLobbyResult.Created;

        return !userFree.WasSatisfied ? CreateLobbyResult.UserAlreadyInLobby
            : codeFree is { WasSatisfied: false } ? CreateLobbyResult.CodeTaken
            : throw new InvalidOperationException("Redis transaction for lobby creation was aborted.");
    }

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

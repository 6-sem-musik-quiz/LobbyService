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
// Waiting lobbies expire after 2 hours without changes; every write renews the TTL.
public class LobbyCacheRepository(IConnectionMultiplexer redis) : ILobbyCacheRepository
{
    public static readonly TimeSpan WaitingLobbyTtl = TimeSpan.FromHours(2);

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

        if (await tran.ExecuteAsync())
            return CreateLobbyResult.Created;

        return !userFree.WasSatisfied ? CreateLobbyResult.UserAlreadyInLobby
            : codeFree is { WasSatisfied: false } ? CreateLobbyResult.CodeTaken
            : throw new InvalidOperationException("Redis transaction for lobby creation was aborted.");
    }
}

using System.Text.Json.Serialization;

namespace LobbyService.Domain.Entities;

// Active lobby, stored as JSON in Redis under lobby:{id}. LobbyService has no SQL database.
public class Lobby
{
    public Guid Id { get; set; }

    // Join code, only set for private lobbies. 6 chars from A-Z2-9 without 0/O/1/I.
    public string? Code { get; set; }

    public int HostUserId { get; set; }
    public bool IsPrivate { get; set; }
    public LobbySettings Settings { get; set; } = new();

    // The host is always first.
    public List<LobbyPlayer> Players { get; set; } = [];

    public LobbyStatus Status { get; set; } = LobbyStatus.Waiting;
    public Guid? GameId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // SCRUM-172: after a game the lobby reopens with no players. The players of that game wait here
    // until they press "Spil igen" (POST /api/lobbies/{id}/play-again), which moves them back into
    // Players. Whoever leaves or closes the page simply never comes back. Null when nobody is pending:
    // the property is then left out of the stored JSON, so lobbies written before this field existed
    // still compare equal in the compare-and-swap writes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LobbyPlayer>? PendingReturn { get; set; }
}

public class LobbySettings
{
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public int AnswerTimeSeconds { get; set; }
    public int Rounds { get; set; }
    public int MaxPlayers { get; set; }
}

public class LobbyPlayer
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public bool IsReady { get; set; }
}

public enum LobbyStatus
{
    Waiting,
    InGame
}

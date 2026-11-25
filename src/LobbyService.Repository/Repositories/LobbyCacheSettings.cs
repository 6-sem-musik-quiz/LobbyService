namespace LobbyService.Repository.Repositories;

// SCRUM-160: a waiting lobby with no activity for IdleTimeout is removed from Redis (its keys expire).
// Configured with Lobby:IdleTimeoutMinutes (env Lobby__IdleTimeoutMinutes).
public sealed record LobbyCacheSettings(TimeSpan IdleTimeout)
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);
    public static readonly LobbyCacheSettings Default = new(DefaultIdleTimeout);

    public static LobbyCacheSettings FromMinutes(int? minutes) =>
        minutes is > 0 ? new(TimeSpan.FromMinutes(minutes.Value)) : Default;
}

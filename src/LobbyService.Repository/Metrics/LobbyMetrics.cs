using System.Diagnostics.Metrics;

namespace LobbyService.Repository.Metrics;

// Lobby counts by status, as read from Redis.
public readonly record struct LobbyCounts(long Waiting, long InGame);

// OpenTelemetry instruments for LobbyService (meter "Slopster.Lobby").
//   slopster_lobby_cache_lookups_total{result=hit|miss}  counter, one per lookup in LobbyCacheRepository
//   slopster_lobbies_active{status=waiting|in_game}      observable gauge
// The gauge callback is synchronous, so it reports the last snapshot that LobbyCountsRefresher
// stored with Update. Without a snapshot (Redis unreachable) it reports nothing.
public sealed class LobbyMetrics : IDisposable
{
    public const string MeterName = "Slopster.Lobby";
    public const string LobbiesActiveName = "slopster_lobbies_active";
    public const string CacheLookupsName = "slopster_lobby_cache_lookups_total";

    private readonly Counter<long> _lookups;
    private LobbyCounts? _snapshot;

    public LobbyMetrics()
    {
        Meter = new Meter(MeterName);
        _lookups = Meter.CreateCounter<long>(CacheLookupsName, description: "Lookups in the lobby cache (Redis), by result.");
        Meter.CreateObservableGauge(LobbiesActiveName, ObserveLobbies, description: "Number of lobbies, by status.");
    }

    public Meter Meter { get; }

    public void RecordLookup(bool hit) =>
        _lookups.Add(1, new KeyValuePair<string, object?>("result", hit ? "hit" : "miss"));

    // null = the count is unknown (e.g. Redis error); the gauge then reports nothing.
    public void Update(LobbyCounts? counts) => _snapshot = counts;

    private IEnumerable<Measurement<long>> ObserveLobbies()
    {
        if (_snapshot is not { } counts)
            return [];
        return
        [
            new Measurement<long>(counts.Waiting, new KeyValuePair<string, object?>("status", "waiting")),
            new Measurement<long>(counts.InGame, new KeyValuePair<string, object?>("status", "in_game"))
        ];
    }

    public void Dispose() => Meter.Dispose();
}

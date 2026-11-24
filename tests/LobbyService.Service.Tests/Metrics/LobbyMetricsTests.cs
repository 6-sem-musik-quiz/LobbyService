using System.Diagnostics.Metrics;
using LobbyService.Repository.Interfaces;
using LobbyService.Repository.Metrics;
using LobbyService.Repository.Repositories;
using LobbyService.Service.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace LobbyService.Service.Tests.Metrics;

public class LobbyMetricsTests
{
    // Collects the measurements one LobbyMetrics instance reports (other tests run in parallel).
    private sealed class Collector : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<(string Instrument, long Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

        public Collector(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
            _listener.Start();
        }

        public void Observe() => _listener.RecordObservableInstruments();

        public long Sum(string instrument, string tag, string tagValue) =>
            Measurements.Where(m => m.Instrument == instrument && (string?)m.Tags[tag] == tagValue).Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }

    private static (LobbyCacheRepository Repository, IDatabase Db, LobbyMetrics Metrics) CreateRepository()
    {
        var db = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        var metrics = new LobbyMetrics();
        return (new LobbyCacheRepository(redis, metrics), db, metrics);
    }

    [Fact]
    public async Task Lookup_that_finds_the_lobby_counts_as_hit()
    {
        var (repository, db, metrics) = CreateRepository();
        using var collector = new Collector(metrics.Meter);
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue("""{"id":"00000000-0000-0000-0000-000000000001"}""")));

        await repository.GetAsync(Guid.NewGuid());

        Assert.Equal(1, collector.Sum(LobbyMetrics.CacheLookupsName, "result", "hit"));
        Assert.Equal(0, collector.Sum(LobbyMetrics.CacheLookupsName, "result", "miss"));
    }

    [Fact]
    public async Task Lookup_that_finds_nothing_counts_as_miss()
    {
        var (repository, db, metrics) = CreateRepository();
        using var collector = new Collector(metrics.Meter);
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult(RedisValue.Null));

        await repository.GetAsync(Guid.NewGuid());
        await repository.GetLobbyIdByCodeAsync("ABCDE");
        await repository.GetLobbyIdForUserAsync(7);

        Assert.Equal(3, collector.Sum(LobbyMetrics.CacheLookupsName, "result", "miss"));
        Assert.Equal(0, collector.Sum(LobbyMetrics.CacheLookupsName, "result", "hit"));
    }

    [Fact]
    public async Task Code_and_user_lookups_count_hits()
    {
        var (repository, db, metrics) = CreateRepository();
        using var collector = new Collector(metrics.Meter);
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue(Guid.NewGuid().ToString())));

        await repository.GetLobbyIdByCodeAsync("ABCDE");
        await repository.GetLobbyIdForUserAsync(7);

        Assert.Equal(2, collector.Sum(LobbyMetrics.CacheLookupsName, "result", "hit"));
    }

    [Fact]
    public async Task Gauge_reports_the_counts_the_repository_returns()
    {
        var repository = Substitute.For<ILobbyCacheRepository>();
        repository.CountByStatusAsync().Returns(new LobbyCounts(3, 2));
        var (refresher, metrics) = CreateRefresher(repository);
        using var collector = new Collector(metrics.Meter);

        await refresher.RefreshAsync();
        collector.Observe();

        Assert.Equal(3, collector.Sum(LobbyMetrics.LobbiesActiveName, "status", "waiting"));
        Assert.Equal(2, collector.Sum(LobbyMetrics.LobbiesActiveName, "status", "in_game"));
    }

    [Fact]
    public async Task Gauge_reports_nothing_before_the_first_refresh_and_when_the_repository_fails()
    {
        var repository = Substitute.For<ILobbyCacheRepository>();
        var (refresher, metrics) = CreateRefresher(repository);
        using var collector = new Collector(metrics.Meter);

        collector.Observe();
        Assert.Empty(collector.Measurements);

        repository.CountByStatusAsync().Returns(new LobbyCounts(1, 1));
        await refresher.RefreshAsync();
        repository.CountByStatusAsync().ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        await refresher.RefreshAsync();
        collector.Measurements.Clear();
        collector.Observe();

        Assert.Empty(collector.Measurements);
    }

    private static (LobbyCountsRefresher Refresher, LobbyMetrics Metrics) CreateRefresher(ILobbyCacheRepository repository)
    {
        var services = new ServiceCollection().AddScoped(_ => repository).BuildServiceProvider();
        var metrics = new LobbyMetrics();
        var refresher = new LobbyCountsRefresher(
            services.GetRequiredService<IServiceScopeFactory>(), metrics, NullLogger<LobbyCountsRefresher>.Instance);
        return (refresher, metrics);
    }
}

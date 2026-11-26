namespace LobbyService.Service.Messaging;

// Section "RabbitMQ" (SCRUM-172). In the cluster Host/Username/Password come from the
// lobbyservice-secrets secret (gitops) as RabbitMQ__Host etc.
public class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    // Off in Development (appsettings.Development.json) and in the tests, so a local run never takes
    // messages from a real queue.
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    // Must match what GameService publishes (contract gameservice.game-finished 1.1 in Platform).
    public string ExchangeName { get; set; } = "game-events";
    public string RoutingKey { get; set; } = "game.finished.v1";

    // Owned by LobbyService. Its own queue, so StatisticService's queue still gets every message too.
    // Durable: a game that ends while every LobbyService pod is down still reopens its lobby afterwards.
    public string QueueName { get; set; } = "lobbyservice.game-finished";

    public ushort PrefetchCount { get; set; } = 10;
    public int ReconnectDelaySeconds { get; set; } = 5;
}

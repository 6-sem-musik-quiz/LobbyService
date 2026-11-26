using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using RabbitMQ.Client;

namespace LobbyService.Api.Tests;

// SCRUM-172 through a real RabbitMQ: the GameFinishedConsumer runs, a GameFinished message is published
// on the "game-events" exchange exactly like GameService does, and the lobby reopens.
// Skipped unless LOBBY_TEST_RABBITMQ is set (CI has no broker), e.g.
//   docker run -d --rm -p 5673:5672 --name lobby-test-rabbit rabbitmq:4-alpine
//   LOBBY_TEST_RABBITMQ=localhost:5673 dotnet test -c Release --filter RabbitMqPlayAgainTests
public class RabbitMqPlayAgainTests : IAsyncLifetime
{
    private static readonly string? Broker = Environment.GetEnvironmentVariable("LOBBY_TEST_RABBITMQ");
    // Own queue per run, so a leftover queue from an earlier run or a real LobbyService never takes the message.
    private readonly string _queue = $"lobbyservice.game-finished.test-{Guid.NewGuid():N}";
    private RabbitFactory? _factory;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        if (Broker is null) return;
        await using var connection = await Connect();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(_queue);
    }

    [RabbitFact]
    public async Task GameFinishedOnTheExchange_ReopensTheLobby()
    {
        _factory = new RabbitFactory(Broker!, _queue);
        var host = Client(5701);
        var guest = Client(5702);

        // A started game.
        var create = await host.PostAsJsonAsync("/api/lobbies", new
        {
            categoryId = LobbyApiFactory.KnownPlaylistId, answerTimeSeconds = 30, rounds = 10, maxPlayers = 4, isPrivate = true
        });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var lobbyId = created.GetProperty("id").GetGuid();
        await guest.PostAsJsonAsync("/api/lobbies/join-by-code", new { code = created.GetProperty("code").GetString() });
        await host.PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });
        await guest.PostAsJsonAsync($"/api/lobbies/{lobbyId}/ready", new { ready = true });
        var start = await host.PostAsync($"/api/lobbies/{lobbyId}/start", null);
        var gameId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gameId").GetGuid();

        await WaitForQueue(); // the consumer has declared and bound its queue
        await Publish(new { gameId, lobbyId, rounds = 10, startTime = DateTime.UtcNow, endTime = DateTime.UtcNow,
            catId = 1, categoryName = "Test playlist", players = Array.Empty<object>() });

        // Like the frontend: try again for a few seconds while the message is on its way.
        HttpResponseMessage? back = null;
        for (var i = 0; i < 50 && back?.StatusCode != HttpStatusCode.OK; i++)
        {
            if (i > 0) await Task.Delay(200);
            back = await host.PostAsync($"/api/lobbies/{lobbyId}/play-again", null);
        }

        Assert.Equal(HttpStatusCode.OK, back!.StatusCode);
        var lobby = await back.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Waiting", lobby.GetProperty("status").GetString());
    }

    private HttpClient Client(int userId)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", LobbyApiFactory.CreateToken(userId, $"p{userId}"));
        return client;
    }

    private static Task<IConnection> Connect()
    {
        var (host, port) = Split(Broker!);
        return new ConnectionFactory { HostName = host, Port = port }.CreateConnectionAsync();
    }

    private static (string Host, int Port) Split(string broker)
    {
        var parts = broker.Split(':');
        return (parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 5672);
    }

    private async Task WaitForQueue()
    {
        await using var connection = await Connect();
        for (var i = 0; i < 50; i++)
        {
            await using var channel = await connection.CreateChannelAsync();
            try
            {
                if ((await channel.QueueDeclarePassiveAsync(_queue)).ConsumerCount > 0) return;
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                // not declared yet (the passive declare closes the channel)
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("The consumer never declared its queue.");
    }

    // The same exchange, routing key and properties as GameService's ResultPublisher.
    private static async Task Publish(object message)
    {
        await using var connection = await Connect();
        await using var channel = await connection.CreateChannelAsync();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await channel.BasicPublishAsync("game-events", "game.finished.v1", mandatory: false,
            new BasicProperties { DeliveryMode = DeliveryModes.Persistent, ContentType = "application/json" }, body);
    }

    private sealed class RabbitFactory(string broker, string queue) : LobbyApiFactory
    {
        protected override void ConfigureRabbitMq(IWebHostBuilder builder)
        {
            var (host, port) = Split(broker);
            builder.UseSetting("RabbitMQ:Enabled", "true");
            builder.UseSetting("RabbitMQ:Host", host);
            builder.UseSetting("RabbitMQ:Port", port.ToString());
            builder.UseSetting("RabbitMQ:QueueName", queue);
            builder.UseSetting("RabbitMQ:ReconnectDelaySeconds", "1");
        }
    }

    private sealed class RabbitFactAttribute : FactAttribute
    {
        public RabbitFactAttribute()
        {
            if (string.IsNullOrEmpty(Broker))
                Skip = "Set LOBBY_TEST_RABBITMQ (e.g. localhost:5673) to run the RabbitMQ test.";
        }
    }
}

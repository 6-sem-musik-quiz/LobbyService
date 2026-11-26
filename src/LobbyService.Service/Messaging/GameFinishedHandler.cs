using System.Text.Json;
using LobbyService.Service.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LobbyService.Service.Messaging;

public enum DeliveryOutcome
{
    Ack,      // handled: the lobby was reopened, or there was nothing to reopen
    Reject,   // invalid message: nack without requeue, it is dropped
    Requeue   // could not handle it right now (e.g. Redis down): nack with requeue
}

// The part of GameService's GameFinished message LobbyService needs (contract gameservice.game-finished 1.1).
// The rest of the message (players, scores) is StatisticService's business and is ignored here.
public record GameFinishedMessage(Guid GameId, Guid? LobbyId);

// SCRUM-172: turns one GameFinished delivery into a reopened lobby and decides what to tell RabbitMQ.
// Kept apart from the RabbitMQ plumbing in GameFinishedConsumer so it can be tested without a broker.
public class GameFinishedHandler(IServiceScopeFactory scopes, ILogger<GameFinishedHandler> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DeliveryOutcome> HandleAsync(ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        GameFinishedMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<GameFinishedMessage>(body.Span, Json);
        }
        catch (JsonException e)
        {
            logger.LogWarning(e, "Dropped a GameFinished message that is not valid JSON");
            return DeliveryOutcome.Reject;
        }

        if (message is null || message.GameId == Guid.Empty)
        {
            logger.LogWarning("Dropped a GameFinished message without gameId");
            return DeliveryOutcome.Reject;
        }

        // A v1 message (from a GameService older than SCRUM-172) has no lobbyId: nothing to reopen.
        if (message.LobbyId is not { } lobbyId || lobbyId == Guid.Empty)
        {
            logger.LogInformation("GameFinished for game {GameId} has no lobbyId; no lobby to reopen", message.GameId);
            return DeliveryOutcome.Ack;
        }

        try
        {
            // LobbyManager is scoped: one scope per message.
            await using var scope = scopes.CreateAsyncScope();
            var lobbies = scope.ServiceProvider.GetRequiredService<ILobbyManager>();
            await lobbies.ReopenAfterGameAsync(lobbyId, message.GameId);
            return DeliveryOutcome.Ack;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DeliveryOutcome.Requeue;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not reopen lobby {LobbyId} after game {GameId}; it goes back on the queue",
                lobbyId, message.GameId);
            return DeliveryOutcome.Requeue;
        }
    }
}

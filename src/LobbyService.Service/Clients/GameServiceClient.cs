using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LobbyService.Domain.Dtos;
using LobbyService.Domain.Exceptions;
using LobbyService.Service.Interfaces;

namespace LobbyService.Service.Clients;

// Typed HttpClient for GameService. Base address comes from Services:GameService (see AddServiceLayer).
public class GameServiceClient(HttpClient httpClient) : IGameServiceClient
{
    public async Task<Guid> CreateGameAsync(CreateGameRequest request, string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/games")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                && await MaintenanceMessageAsync(response, cancellationToken) is { } maintenance)
                throw new ServiceInMaintenanceException("gameservice", maintenance);
            if (!response.IsSuccessStatusCode)
                throw new GameServiceUnavailableException(
                    $"GameService returned {(int)response.StatusCode} when creating the game.");

            var body = await response.Content.ReadFromJsonAsync<CreateGameResponse>(cancellationToken);
            return body is { GameId: var id } && id != Guid.Empty
                ? id
                : throw new GameServiceUnavailableException("GameService did not return a game id.");
        }
        catch (HttpRequestException e)
        {
            throw new GameServiceUnavailableException("GameService could not be reached.", e);
        }
        catch (JsonException e)
        {
            throw new GameServiceUnavailableException("GameService returned an invalid response.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GameServiceUnavailableException("GameService timed out.", e);
        }
    }

    // SCRUM-179: GameService answers 503 with the maintenance ProblemDetails when an admin has
    // switched it off. Returns its detail, or null for any other 503 (then it stays a 502).
    private static async Task<string?> MaintenanceMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return problem.ValueKind == JsonValueKind.Object
                   && problem.TryGetProperty("type", out var type)
                   && type.GetString() == ServiceInMaintenanceException.ProblemType
                ? (problem.TryGetProperty("detail", out var detail) ? detail.GetString() : null) ?? "Game is under maintenance."
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

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
}

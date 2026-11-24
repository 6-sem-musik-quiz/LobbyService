using LobbyService.Service.Clients;
using LobbyService.Service.Interfaces;
using LobbyService.Service.Metrics;
using LobbyService.Service.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LobbyService.Service.Extensions;

public static class ServiceLayerExtensions
{
    public static IServiceCollection AddServiceLayer(this IServiceCollection services, string? musicServiceBaseUrl,
        string? gameServiceBaseUrl = null)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILobbyCodeGenerator, LobbyCodeGenerator>();
        services.AddScoped<ILobbyManager, LobbyManager>();
        services.AddHostedService<LobbyCountsRefresher>();

        services.AddHttpClient<IMusicServiceClient, MusicServiceClient>(client =>
            client.BaseAddress = ToBaseAddress(musicServiceBaseUrl, "http://localhost:5002"));
        services.AddHttpClient<IGameServiceClient, GameServiceClient>(client =>
            client.BaseAddress = ToBaseAddress(gameServiceBaseUrl, "http://localhost:5005"));

        return services;
    }

    private static Uri ToBaseAddress(string? configured, string fallback)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        return new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    }
}

using LobbyService.Service.Clients;
using LobbyService.Service.Interfaces;
using LobbyService.Service.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LobbyService.Service.Extensions;

public static class ServiceLayerExtensions
{
    public static IServiceCollection AddServiceLayer(this IServiceCollection services, string? musicServiceBaseUrl)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILobbyCodeGenerator, LobbyCodeGenerator>();
        services.AddScoped<ILobbyManager, LobbyManager>();

        services.AddHttpClient<IMusicServiceClient, MusicServiceClient>(client =>
        {
            var baseUrl = musicServiceBaseUrl ?? "http://localhost:5002";
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        });

        return services;
    }
}

using LobbyService.Service.Clients;
using LobbyService.Service.Interfaces;
using LobbyService.Service.Messaging;
using LobbyService.Service.Metrics;
using LobbyService.Service.Services;
using Microsoft.Extensions.Configuration;
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

    // SCRUM-172: the RabbitMQ consumer that reopens a lobby when its game has finished. Settings from the
    // "RabbitMQ" section; RabbitMQ:Enabled=false registers it but makes it do nothing.
    public static IServiceCollection AddGameFinishedConsumer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddSingleton<GameFinishedHandler>();
        services.AddHostedService<GameFinishedConsumer>();
        return services;
    }

    private static Uri ToBaseAddress(string? configured, string fallback)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        return new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    }
}

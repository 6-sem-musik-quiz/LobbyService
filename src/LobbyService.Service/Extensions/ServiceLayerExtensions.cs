using LobbyService.Service.Interfaces;
using LobbyService.Service.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LobbyService.Service.Extensions;

public static class ServiceLayerExtensions
{
    public static IServiceCollection AddServiceLayer(this IServiceCollection services)
    {
        services.AddScoped<IExampleService, ExampleService>();

        return services;
    }
}

using LobbyService.Repository.Data;
using LobbyService.Repository.Interfaces;
using LobbyService.Repository.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LobbyService.Repository.Extensions;

public static class RepositoryLayerExtensions
{
    public static IServiceCollection AddRepositoryLayer(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IExampleRepository, ExampleRepository>();

        return services;
    }
}

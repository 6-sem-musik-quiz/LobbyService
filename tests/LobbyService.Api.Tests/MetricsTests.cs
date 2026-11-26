using LobbyService.Repository.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace LobbyService.Api.Tests;

public class MetricsTests(LobbyApiFactory factory) : IClassFixture<LobbyApiFactory>
{
    [Fact]
    public void App_starts_with_metrics_registered()
    {
        using var client = factory.CreateClient(); // starts Program, including the OpenTelemetry setup

        Assert.NotNull(factory.Services.GetService<MeterProvider>());
        Assert.NotNull(factory.Services.GetService<TracerProvider>()); // SCRUM-173
        Assert.NotNull(factory.Services.GetService<LobbyMetrics>());
    }
}

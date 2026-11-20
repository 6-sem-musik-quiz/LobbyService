using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LobbyService.Api.Tests;

public class HealthCheckTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    // Port 1 is never Redis, so the connection is refused at once.
    private const string UnreachableRedis = "127.0.0.1:1,connectTimeout=500,asyncTimeout=500";

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReturnsOk_WhenRedisIsDown()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Redis", UnreachableRedis)).CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Returns503_WhenRedisIsDown()
    {
        var client = factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Redis", UnreachableRedis)).CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

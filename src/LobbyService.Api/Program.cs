using System.Text;
using System.Text.Json.Serialization;
using LobbyService.Api.HealthChecks;
using LobbyService.Api.Middleware;
using LobbyService.Repository.Extensions;
using LobbyService.Service.Extensions;
using LobbyService.Service.Hubs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Connections;
using StackExchange.Redis;
using LobbyService.Repository.Metrics;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
// /health is liveness (the process is up); /health/ready also checks
// that Redis answers, so a pod without Redis gets no traffic.
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

// JWT issued by UserService (same validation as SCRUM-88). User id from "sub", name from "unique_name".
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IConfiguration>((options, config) =>
    {
        var signingKey = config["Jwt:SigningKey"];
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config["Jwt:Issuer"] ?? "slopster-userservice",
            ValidateAudience = true,
            ValidAudience = config["Jwt:Audience"] ?? "slopster",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // Without a configured key no token validates, so every protected endpoint returns 401.
            IssuerSigningKey = string.IsNullOrEmpty(signingKey)
                ? null
                : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            NameClaimType = "unique_name"
        };
        // Browsers cannot set headers on WebSockets, so SignalR sends the token as ?access_token=.
        // Only accepted for the hub, so tokens in query strings are not allowed on the REST API.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken)
                    && (path.StartsWithSegments(LobbyHub.Path) || path.StartsWithSegments(LobbyHub.EdgePath)))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// Enums as strings, same as the REST API ("Waiting", "InGame").
// Redis backplane (SCRUM-128): every pod publishes its group messages on Redis and receives the
// others', so LobbyUpdated reaches a player whose WebSocket sits on another pod. Same Redis as the
// lobbies; the channel prefix keeps the SignalR channels apart from anything else using it.
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379", o =>
    {
        o.Configuration.ChannelPrefix = RedisChannel.Literal(LobbyHub.BackplaneChannelPrefix);
        o.Configuration.AbortOnConnectFail = false; // start (and serve /health) even when Redis is down
    });

builder.Services.AddRepositoryLayer(builder.Configuration.GetConnectionString("Redis"),
    builder.Configuration.GetValue<int?>("Lobby:IdleTimeoutMinutes")); // env Lobby__IdleTimeoutMinutes, default 10
builder.Services.AddServiceLayer(
    builder.Configuration["MusicService:BaseUrl"],
    builder.Configuration["Services:GameService"]); // env Services__GameService
// SCRUM-172: GameFinished from RabbitMQ reopens the lobby ("Spil igen"). Env RabbitMQ__Host etc.
builder.Services.AddGameFinishedConsumer(builder.Configuration);

// Metrics over OTLP. The collector address comes from OTEL_EXPORTER_OTLP_ENDPOINT (default
// http://localhost:4317) and the service name from OTEL_SERVICE_NAME.
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation() // http.server.request.duration
        .AddMeter(LobbyMetrics.MeterName)
        .AddOtlpExporter());

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
// Requires a JWT through the fallback policy. The edge forwards /api/lobbyservice/hubs/lobby as
// /api/hubs/lobby (it turns /api/lobbyservice/{rest} into /api/{rest}), so the hub is mapped there too.
// WebSockets only (SCRUM-128). With more than one pod, a negotiate request and the connection that
// follows can land on different pods, and only the pod that answered negotiate knows the connection
// id. The frontend therefore skips negotiate and opens the WebSocket directly, which is one request
// to one pod. Long polling and SSE (several requests per connection) are turned off.
app.MapHub<LobbyHub>(LobbyHub.Path, o => o.Transports = HttpTransportType.WebSockets);
app.MapHub<LobbyHub>(LobbyHub.EdgePath, o => o.Transports = HttpTransportType.WebSockets);
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;

# LobbyService

Part of the **6-sem-musik-quiz** system. ASP.NET Core (.NET 10) Web API using MVC controllers and a layered architecture.

## Architecture

```
Api  ──►  Service  ──►  Repository
  └─────────┴──────────────┴──►  Domain
```

| Project | Responsibility |
|---|---|
| `src/LobbyService.Api` | Controllers, middleware, `Program.cs`. Thin: validate input, call a service, return HTTP result. |
| `src/LobbyService.Service` | Business logic. Talks to repositories through interfaces, returns DTOs. |
| `src/LobbyService.Repository` | Data access. No SQL database (see the C4 design); active lobbies live in Redis (`LobbyCacheRepository`, StackExchange.Redis). |
| `src/LobbyService.Domain` | Entities and DTOs. No dependencies. |
| `tests/LobbyService.Service.Tests` | Unit tests for services (xUnit + NSubstitute). |
| `tests/LobbyService.Api.Tests` | Integration tests using `WebApplicationFactory`. |


## API

All endpoints require a JWT from UserService (`Authorization: Bearer <token>`). Through the edge they are exposed as `/api/lobbyservice/...`.

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/lobbies` | Create a lobby. Body `{ categoryId, answerTimeSeconds, rounds, maxPlayers, isPrivate }`. `answerTimeSeconds` ∈ {15, 30, 45, 60}, `rounds` ∈ {5, 10, 15, 20}, `maxPlayers` 2–8. The playlist (`categoryId`) is fetched from MusicService and must have at least `rounds + 1` songs. Returns `201` with a `LobbyDto`, `400` for invalid values, an unknown playlist or too few songs, `401` without a valid token, and `409` if the user is already in a lobby. |

### Redis keys

| Key | Value |
|---|---|
| `lobby:{id}` | Lobby as JSON |
| `lobbycode:{code}` | Lobby id (private lobbies only; 6 chars from `A-Z2-9` without `0/O/1/I`) |
| `user-lobby:{userId}` | Id of the lobby the user is in |

Waiting lobbies expire after 2 hours without changes. Every write renews the TTL.

## Configuration

| Key | Default | Description |
|---|---|---|
| `ConnectionStrings:Redis` | `localhost:6379` | Redis connection string |
| `MusicService:BaseUrl` | `http://localhost:5002` | MusicService base URL (used for `GET /api/playlists/{id}`) |
| `Jwt:SigningKey` | *(empty)* | Same symmetric key as UserService. Set it via user secrets or an environment variable (`Jwt__SigningKey`), never in `appsettings.json`. Without it every token is rejected. |
| `Jwt:Issuer` | `slopster-userservice` | Expected token issuer |
| `Jwt:Audience` | `slopster` | Expected token audience |

## Running locally

**Everything in Docker:**

```bash
docker compose up --build
```

**API from your IDE:**

```bash
dotnet run --project src/LobbyService.Api
```

- API: http://localhost:5004
- Swagger: http://localhost:5004/swagger
- Health: http://localhost:5004/health

## Tests

```bash
dotnet test
```

## Service ports

| Service | API | Postgres |
|---|---|---|
| UserService | 5001 | 5441 |
| MusicService | 5002 | 5442 |
| StatisticService | 5003 | 5443 |
| LobbyService | 5004 | – |
| GameService | 5005 | – |

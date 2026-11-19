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
| `src/LobbyService.Repository` | Data access. No SQL database (see the C4 design); empty for now. |
| `src/LobbyService.Domain` | Entities and DTOs. No dependencies. |
| `tests/LobbyService.Service.Tests` | Unit tests for services (xUnit + NSubstitute). |
| `tests/LobbyService.Api.Tests` | Integration tests using `WebApplicationFactory`. |


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

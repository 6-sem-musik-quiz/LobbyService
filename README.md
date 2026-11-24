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
| `GET` | `/api/lobbies` | Public lobbies waiting for players, newest first. Full lobbies are included (the frontend shows them as full). Returns `200` with `[{ id, hostUserName, categoryName, playerCount, maxPlayers, rounds, answerTimeSeconds }]` (empty array if none) and `401` without a valid token. |
| `GET` | `/api/lobbies/mine` | The waiting lobby the caller is in, so a player who left the page can find it again. Returns `200` with a `LobbyDto`, `401` without a valid token, and `404` if the caller is in no lobby. A `user-lobby` key whose lobby is gone, has started or no longer lists the caller is deleted (also when creating or joining), so a stale reference never blocks the player. |
| `GET` | `/api/lobbies/{id}` | Full `LobbyDto` including players and ready status. Returns `200`, `401` without a valid token, and `404` if the lobby does not exist or is private and the caller is not one of its players. |
| `POST` | `/api/lobbies/{id}/ready` | Body `{ ready: bool }`. Sets the caller's ready flag. Returns `200` with the `LobbyDto`, `404` if the lobby does not exist or the caller is not in it, and `409` if the game has started. |
| `POST` | `/api/lobbies/{id}/start` | Host only. Calls GameService `POST /api/games` with `{ lobbyId, categoryId, categoryName, rounds, answerTimeSeconds, players: [{ userId, userName }] }` and the host's token. Returns `200` with `{ gameId }`, `403` if the caller is not host, `404` if the caller is not in the lobby, `409` with fewer than 2 players, not everyone ready or the game already started, and `502` if GameService fails (the lobby stays `Waiting`). On success the lobby becomes `InGame` with `gameId`, leaves `lobbies:public`, its code and every player's `user-lobby` key are deleted (so they can join new lobbies), and it expires 10 minutes later. |

### Live updates (SignalR)

Hub: `/hubs/lobby`, and `/api/hubs/lobby`, which is where the edge forwards the public path `/api/lobbyservice/hubs/lobby` (the edge strips the service name). Requires the same JWT; for WebSockets the token is sent as `?access_token=<token>` (only accepted on the hub path). Enums are serialised as strings, as in REST.

| Direction | Method | Description |
|---|---|---|
| client → server | `JoinLobbyGroup(Guid lobbyId)` | Subscribe to group `lobby:{id}`. Throws `HubException` if the caller is not in the lobby. Call after joining via REST and again after a reconnect. |
| client → server | `LeaveLobbyGroup(Guid lobbyId)` | Unsubscribe. Does not leave the lobby (use `POST /api/lobbies/{id}/leave`). |
| server → group | `LobbyUpdated(LobbyDto)` | After a join, leave, ready change or host change. Not sent for no-ops (already joined, ready flag unchanged). |
| server → group | `GameStarted({ gameId })` | After the host has started the game. |
| server → group | `LobbyClosed()` | When the last player left and the lobby was deleted. |

`LobbyManager` sends the messages via `IHubContext` after the change is stored; a failed send is logged and does not fail the request. A dropped connection does not change the lobby. With more than one replica a SignalR backplane (e.g. Redis) is needed so every pod reaches every group.

### Redis keys

| Key | Value |
|---|---|
| `lobby:{id}` | Lobby as JSON |
| `lobbycode:{code}` | Lobby id (private lobbies only; 6 chars from `A-Z2-9` without `0/O/1/I`) |
| `user-lobby:{userId}` | Id of the lobby the user is in |
| `lobbies:public` | Set of ids of public lobbies with status `Waiting`, so `GET /api/lobbies` needs no `SCAN` |

Waiting lobbies expire after 2 hours without changes. Every write renews the TTL. A started (`InGame`) lobby expires 10 minutes after the start.

`lobbies:public` has no TTL. A lobby id is added when a public lobby is created and must be removed (`RemoveFromPublicAsync`) when the lobby starts, becomes private or is deleted. Ids whose lobby has expired or is no longer public and waiting are also pruned when the list is read.

## Configuration

| Key | Default | Description |
|---|---|---|
| `ConnectionStrings:Redis` | `localhost:6379` | Redis connection string |
| `MusicService:BaseUrl` | `http://localhost:5002` | MusicService base URL (used for `GET /api/playlists/{id}`, which returns `{ id, name, songCount }` per SCRUM-92) |
| `Services:GameService` | `http://localhost:5005` | GameService base URL (env `Services__GameService`), used for `POST /api/games` when the host starts the game |
| `Jwt:SigningKey` | *(empty)* | Same symmetric key as UserService. It lives in OpenBao at `kv/quiz/jwt` (property `signing-key`); in the cluster it arrives as `Jwt__SigningKey` via ExternalSecret. Locally, put it in `.env` (see below), never in `appsettings.json`. Without it every token is rejected. |
| `Jwt:Issuer` | `slopster-userservice` | Expected token issuer |
| `Jwt:Audience` | `slopster` | Expected token audience |

## Running locally

The JWT signing key is a secret, so each developer keeps it in a local `.env` file
(in `.gitignore`). Compose refuses to start without it.

```powershell
Copy-Item .env.example .env
# fill in JWT_SIGNING_KEY: bao kv get -field=signing-key kv/quiz/jwt
```

**Everything in Docker:**

```bash
docker compose up --build
```

**API from your IDE:** set the key as an environment variable first.

```powershell
$env:Jwt__SigningKey = "<key>"
dotnet run --project src/LobbyService.Api
```

- API: http://localhost:5004
- Swagger: http://localhost:5004/swagger
- Health: http://localhost:5004/health (liveness), http://localhost:5004/health/ready (also checks Redis)

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

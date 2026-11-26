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
| `POST` | `/api/lobbies/{id}/start` | Host only. Calls GameService `POST /api/games` with `{ lobbyId, categoryId, categoryName, rounds, answerTimeSeconds, players: [{ userId, userName }] }` and the host's token. Returns `200` with `{ gameId }`, `403` if the caller is not host, `404` if the caller is not in the lobby, `409` with fewer than 2 players, not everyone ready or the game already started, and `502` if GameService fails (the lobby stays `Waiting`). On success the lobby becomes `InGame` with `gameId`, leaves `lobbies:public` and every player's `user-lobby` key is deleted (so they can join new lobbies). The code is kept for "Spil igen" (joining a started lobby gives `409`). The lobby lives as long as the game can take (see below) and reopens when the game has finished. |
| `POST` | `/api/lobbies/{id}/play-again` | SCRUM-172, "Spil igen" from the end screen. Puts the caller back into the reopened lobby (same id, code and settings), not ready. Returns `200` with the `LobbyDto` (also when the caller is already back), `404` if the lobby does not exist or the caller did not play its last game, and `409` while the game has not finished yet (GameFinished not received; the frontend retries), when the lobby is full or the caller is in another lobby. |

### Live updates (SignalR)

Hub: `/hubs/lobby`, and `/api/hubs/lobby`, which is where the edge forwards the public path `/api/lobbyservice/hubs/lobby` (the edge strips the service name). Requires the same JWT; for WebSockets the token is sent as `?access_token=<token>` (only accepted on the hub path). Enums are serialised as strings, as in REST.

| Direction | Method | Description |
|---|---|---|
| client → server | `JoinLobbyGroup(Guid lobbyId)` | Subscribe to group `lobby:{id}`. Throws `HubException` if the caller is not in the lobby. Call after joining via REST and again after a reconnect. |
| client → server | `LeaveLobbyGroup(Guid lobbyId)` | Unsubscribe. Does not leave the lobby (use `POST /api/lobbies/{id}/leave`). |
| server → group | `LobbyUpdated(LobbyDto)` | After a join, leave, ready change or host change. Not sent for no-ops (already joined, ready flag unchanged). |
| server → group | `GameStarted({ gameId })` | After the host has started the game. |
| server → group | `LobbyClosed()` | When the last player left and the lobby was deleted. |

`LobbyManager` sends the messages via `IHubContext` after the change is stored; a failed send is logged and does not fail the request. A dropped connection does not change the lobby.

#### More than one pod (SCRUM-128)

LobbyService runs with 2–5 pods (HPA in gitops). Two things make SignalR work across them:

1. **Redis backplane.** `AddSignalR().AddStackExchangeRedis(...)` uses the same Redis as the lobbies (`ConnectionStrings:Redis`) with channel prefix `slopster-lobby`. A pod that sends `LobbyUpdated` to group `lobby:{id}` publishes it on Redis; every pod receives it and delivers it to its own connections in that group. Without it, a player whose WebSocket is on another pod than the one that handled the REST call gets nothing.
2. **WebSockets only, without negotiate.** The hub accepts only the WebSockets transport. The frontend sets `SkipNegotiation = true`, so a connection is a single request (the WebSocket upgrade) that lands on one pod and stays there. With negotiate, the `POST .../negotiate` and the following WebSocket are two requests that the Kubernetes Service may send to different pods, and the second pod rejects the unknown connection id. The alternative, session affinity, was not chosen: EdgeService (YARP) has a single destination (`http://lobbyservice:8080`, the Service), so affinity in the edge would not pin a pod, and `sessionAffinity: ClientIP` on the Service would see the edge pods' IPs and pin everybody to the same few pods. SkipNegotiation needs no state in the edge or the Service.

Group membership lives in the pod that holds the connection. If that pod goes away, the client reconnects (`WithAutomaticReconnect`) to any pod and calls `JoinLobbyGroup` again, as it already does after every reconnect.

**Tests.** `BackplaneTests` runs two "pods" (two `WebApplicationFactory` instances sharing the lobby store) with the host on pod A and the guest on pod B. `WithoutBackplane_PlayerOnOtherPodMissesTheEvent` always runs and shows the problem. `WithRedisBackplane_PlayersOnDifferentPodsSeeEachOthersEvents` needs a real Redis and is skipped unless `LOBBY_TEST_REDIS` is set:

```bash
docker run -d --rm -p 6390:6379 --name lobby-test-redis redis:7-alpine
LOBBY_TEST_REDIS=localhost:6390 dotnet test -c Release --filter BackplaneTests
docker stop lobby-test-redis
```

**Manual test in the cluster.** With 2 pods (`kubectl get pods -n quiz -l app.kubernetes.io/name=lobbyservice`), open the game in two browsers as two users, create a lobby in one and join it in the other, and press "klar" in each: the other browser shows the change at once. Each WebSocket lands on a random pod, so repeat it a few times (reload a browser to get a new connection). Without the backplane, roughly every other attempt shows the change only after a reload; with it, every attempt works.

### Redis keys

| Key | Value |
|---|---|
| `lobby:{id}` | Lobby as JSON |
| `lobbycode:{code}` | Lobby id (private lobbies only; 6 chars from `A-Z2-9` without `0/O/1/I`) |
| `user-lobby:{userId}` | Id of the lobby the user is in |
| `lobbies:public` | Set of ids of public lobbies with status `Waiting`, so `GET /api/lobbies` needs no `SCAN` |
| `lobbies:waiting`, `lobbies:ingame` | Sorted sets of lobby ids (score = expiry, unix seconds) used only for the `slopster_lobbies_active` metric. Expired ids are removed when the metric is read. |

Waiting lobbies expire after 10 minutes without activity (SCRUM-160; `Lobby:IdleTimeoutMinutes`, env `Lobby__IdleTimeoutMinutes`). Every write (create, join, leave, ready, settings) renews the TTL. A started (`InGame`) lobby, and its code, live as long as the game can take: `rounds × (answerTimeSeconds + 30 s) + 10 min` (20 rounds of 60 s give 40 minutes), so a lobby never expires during a game (SCRUM-172). If GameFinished never arrives, the lobby expires after that.

`lobbies:public` has no TTL. A lobby id is added when a public lobby is created and must be removed (`RemoveFromPublicAsync`) when the lobby starts, becomes private or is deleted. Ids whose lobby has expired or is no longer public and waiting are also pruned when the list is read.

### "Spil igen" (SCRUM-172)

When a game is over, GameService publishes `GameFinished` on RabbitMQ (topic exchange `game-events`, routing key `game.finished.v1`, contract `gameservice.game-finished` 1.1 in Platform). Since v1.1 it has the `lobbyId`. LobbyService consumes it from its own durable queue `lobbyservice.game-finished` (`GameFinishedConsumer`), next to StatisticService's queue on the same exchange, so both services get every message. All LobbyService pods share the queue, so one pod handles each message.

```
GameService ──GameFinished──▶ game-events ──▶ statisticservice.game-finished ──▶ StatisticService (saves the result)
                                          └─▶ lobbyservice.game-finished    ──▶ LobbyService (reopens the lobby)
```

1. **GameFinished** for an `InGame` lobby whose `gameId` matches: the lobby becomes `Waiting` with **no players**; the game's players move to `pendingReturn` (stored in `lobby:{id}`, not in the API). Its TTL is the idle timeout again. A repeated or old message (the lobby is not in that game any more) changes nothing, so redelivery is harmless. A message without `lobbyId` (an older GameService) is acked and ignored.
2. **"Spil igen"** (`POST .../play-again`) moves a player from `pendingReturn` back into the lobby, not ready, and sets their `user-lobby` key. Players who press "Forlad" or close the page never come back, so they are simply not in the new lobby. If the old host is not back, the first player back becomes host.
3. Others can **join** the reopened lobby as usual (code, or the public list once someone is back) while there is room. Players who are still pending do not hold a seat.
4. The host **starts** a new game as usual; GameService gives it a new `gameId`. The old game's result was already sent to StatisticService.

The end screen may call `play-again` before the message has been handled (usually well under a second). Then it gets `409` and tries again for a few seconds. If RabbitMQ is down, the consumer reconnects every `RabbitMQ:ReconnectDelaySeconds`; the message waits in the durable queue, and the lobby reopens when it is handled.

**Tests.** `PlayAgainTests` covers game over → lobby reopened → players back → new game started through the API, with the message handed to `GameFinishedHandler` as the consumer does. `RabbitMqPlayAgainTests` does the same through a real broker and is skipped unless `LOBBY_TEST_RABBITMQ` is set:

```bash
docker run -d --rm -p 5673:5672 --name lobby-test-rabbit rabbitmq:4-alpine
LOBBY_TEST_RABBITMQ=localhost:5673 dotnet test -c Release --filter RabbitMqPlayAgainTests
docker stop lobby-test-rabbit
```

## Configuration

| Key | Default | Description |
|---|---|---|
| `ConnectionStrings:Redis` | `localhost:6379` | Redis connection string |
| `MusicService:BaseUrl` | `http://localhost:5002` | MusicService base URL (used for `GET /api/playlists/{id}`, which returns `{ id, name, songCount }` per SCRUM-92) |
| `Services:GameService` | `http://localhost:5005` | GameService base URL (env `Services__GameService`), used for `POST /api/games` when the host starts the game |
| `Jwt:SigningKey` | *(empty)* | Same symmetric key as UserService. It lives in OpenBao at `kv/quiz/jwt` (property `signing-key`); in the cluster it arrives as `Jwt__SigningKey` via ExternalSecret. Locally, put it in `.env` (see below), never in `appsettings.json`. Without it every token is rejected. |
| `Jwt:Issuer` | `slopster-userservice` | Expected token issuer |
| `Jwt:Audience` | `slopster` | Expected token audience |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` | OTLP collector that receives the metrics (standard OpenTelemetry variable) |
| `OTEL_SERVICE_NAME` | *(assembly name)* | Service name on the metrics (standard OpenTelemetry variable) |
| `RabbitMQ:Enabled` | `true` (`false` in Development) | Consume GameFinished (SCRUM-172). Off locally and in the tests |
| `RabbitMQ:Host`, `RabbitMQ:Port` | `localhost`, `5672` | Broker (env `RabbitMQ__Host`); in the cluster from `lobbyservice-secrets` |
| `RabbitMQ:Username`, `RabbitMQ:Password` | `guest` | From OpenBao `kv/quiz/rabbitmq` via ExternalSecret in the cluster |
| `RabbitMQ:QueueName` | `lobbyservice.game-finished` | LobbyService's own durable queue, bound to `game-events` / `game.finished.v1` |

## Traces and logs (SCRUM-173)

Traces and logs go over OTLP with the metrics (`UseOtlpExporter`; endpoint and name from the gitops chart):
HTTP requests (not `/health`), every lobby hub call (`Microsoft.AspNetCore.SignalR.Server`), outgoing HTTP to
GameService and MusicService, and receiving `GameFinished` (`RabbitMQ.Client.Subscriber`). Starting a game calls
GameService over HTTP, so a game's trace starts here, and `GameFinished` comes back in the same trace. Logs carry
the trace id.

## Metrics

Metrics are exported with OpenTelemetry over OTLP (`OTEL_EXPORTER_OTLP_ENDPOINT`) and reach Prometheus through the collector. The meter is `Slopster.Lobby`.

| Metric (Prometheus name) | Type | Attributes | Meaning |
|---|---|---|---|
| `slopster_lobbies_active` | ObservableGauge | `status` = `waiting` \| `in_game` | Number of lobbies. Refreshed from Redis every 20 s; nothing is reported while Redis is unreachable. A started lobby counts as `in_game` until GameFinished reopens it, or until its key expires. |
| `slopster_lobby_cache_lookups_total` | Counter | `result` = `hit` \| `miss` | Lookups in `LobbyCacheRepository` (lobby by id, lobby id by code, lobby id by user). |
| `http_server_request_duration_seconds` | Histogram | `http_route`, `http_response_status_code`, ... | Response times per endpoint, from ASP.NET Core's built-in `http.server.request.duration` (instrumentation is enabled). |

Lobbies that were already in Redis when the sorted sets were introduced are not counted; they expire within the idle timeout.

Example PromQL for Grafana:

```promql
# Active lobbies by status
sum by (status) (slopster_lobbies_active)

# Redis cache hit ratio over 5 minutes
sum(rate(slopster_lobby_cache_lookups_total{result="hit"}[5m]))
  / sum(rate(slopster_lobby_cache_lookups_total[5m]))

# 95th percentile response time per endpoint
histogram_quantile(0.95, sum by (le, http_route) (rate(http_server_request_duration_seconds_bucket[5m])))
```

The Grafana dashboard JSON is not part of this repository.

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

# AGENTS.md — Slopster / LobbyService

This .NET 10 repository is part of the `6-sem-musik-quiz` exam project. Read `README.md` for current architecture, development commands and ports. Cross-service approved knowledge lives in `6-sem-musik-quiz/Platform` (when created); the separate `AgentBlackboard` provides only transient coordination.

## Layer boundaries

- `src/LobbyService.Api`: HTTP controllers, middleware, startup.
- `src/LobbyService.Service`: business logic and interfaces.
- `src/LobbyService.Repository`: data access. LobbyService has no SQL database in the C4 design.
- `src/LobbyService.Domain`: entities and DTOs.
- Tests: `tests/LobbyService.Service.Tests` and `tests/LobbyService.Api.Tests`.

## Build and tests

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

Run locally: `dotnet run --project src/LobbyService.Api`. Review the README before changing ports.
The JWT signing key lives in OpenBao (`kv/quiz/jwt`); locally it comes from `.env`, which must never be committed.

## Four-agent workflow

1. Read the assigned Jira issue in project `SCRUM`, its acceptance criteria and blocking relationships; do not grab unrelated tickets.
2. Read the latest injected Platform contract/ADR context **before** planning. Treat the Platform mirror as read-only. Submit contract changes in a separate Platform PR before editing dependent services.
3. Declare affected paths using `RepoName:path/**`, and have the **worker** claim them against the latest blackboard before edits; if any claim overlaps, stop. An LLM statement is not a lock.
4. Keep changed files inside your claim. Publish relevant questions via the worker's blackboard-update mechanism (if implemented). Do not edit another agent's active note.
5. Work on an isolated branch/worktree, run tests, and open a PR for human review. Do not push to `main`, auto-merge, or set Jira Done yourself.
6. Report test results honestly, including failures. Never commit secrets, private tokens, raw copyrighted songs or local environment files.

The board currently uses `To Do`, `In Progress`, `In Review`, `Done`; verify additional AI statuses are actually created before referring to them in worker configuration.

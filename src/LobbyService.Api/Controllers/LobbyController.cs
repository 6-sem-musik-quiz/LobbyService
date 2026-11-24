using LobbyService.Domain.Dtos;
using LobbyService.Service.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace LobbyService.Api.Controllers;

// Create, join and leave lobbies. All endpoints require a JWT (global fallback policy).
[ApiController]
[Route("api/lobbies")]
public class LobbyController(ILobbyManager lobbyManager) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<LobbyDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LobbyDto>> Create(CreateLobbyRequest request, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirst("sub")?.Value, out var userId))
            return Unauthorized();

        var userName = User.FindFirst("unique_name")?.Value ?? string.Empty;

        var lobby = await lobbyManager.CreateAsync(request, userId, userName, GetAccessToken(), cancellationToken);
        return Created($"/api/lobbies/{lobby.Id}", lobby);
    }

    // Public lobbies waiting for players, newest first. Full lobbies are included.
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<LobbySummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<LobbySummaryDto>>> GetOpen() =>
        Ok(await lobbyManager.GetOpenLobbiesAsync());

    // Private lobbies are only visible to their players (otherwise 404).
    [HttpGet("{id:guid}")]
    [ProducesResponseType<LobbyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LobbyDto>> GetById(Guid id)
    {
        if (!int.TryParse(User.FindFirst("sub")?.Value, out var userId))
            return Unauthorized();

        return Ok(await lobbyManager.GetByIdAsync(id, userId));
    }

    // Joins a public lobby. Private lobbies give 404 here; use join-by-code.
    [HttpPost("{id:guid}/join")]
    [ProducesResponseType<LobbyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LobbyDto>> Join(Guid id)
    {
        if (!TryGetUser(out var userId, out var userName))
            return Unauthorized();

        return Ok(await lobbyManager.JoinAsync(id, userId, userName));
    }

    // Joins a lobby by its code (case-insensitive).
    [HttpPost("join-by-code")]
    [ProducesResponseType<LobbyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LobbyDto>> JoinByCode(JoinByCodeRequest request)
    {
        if (!TryGetUser(out var userId, out var userName))
            return Unauthorized();

        return Ok(await lobbyManager.JoinByCodeAsync(request.Code, userId, userName));
    }

    [HttpPost("{id:guid}/leave")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Leave(Guid id)
    {
        if (!TryGetUser(out var userId, out _))
            return Unauthorized();

        await lobbyManager.LeaveAsync(id, userId);
        return NoContent();
    }

    // Marks the caller ready or not ready.
    [HttpPost("{id:guid}/ready")]
    [ProducesResponseType<LobbyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LobbyDto>> Ready(Guid id, ReadyRequest request)
    {
        if (!TryGetUser(out var userId, out _))
            return Unauthorized();

        return Ok(await lobbyManager.SetReadyAsync(id, userId, request.Ready));
    }

    // The host starts the game once at least 2 players are in the lobby and all are ready.
    // GameService is called with the host's token; if it fails the response is 502 and the lobby stays Waiting.
    [HttpPost("{id:guid}/start")]
    [ProducesResponseType<StartGameResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<StartGameResponse>> Start(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetUser(out var userId, out _))
            return Unauthorized();

        return Ok(await lobbyManager.StartAsync(id, userId, GetAccessToken(), cancellationToken));
    }

    private bool TryGetUser(out int userId, out string userName)
    {
        userName = User.FindFirst("unique_name")?.Value ?? string.Empty;
        return int.TryParse(User.FindFirst("sub")?.Value, out userId);
    }

    // The caller's own JWT, forwarded to MusicService and GameService.
    private string GetAccessToken()
    {
        var authorization = Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : string.Empty;
    }
}

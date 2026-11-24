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
        var authorization = Request.Headers.Authorization.ToString();
        var accessToken = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : string.Empty;

        var lobby = await lobbyManager.CreateAsync(request, userId, userName, accessToken, cancellationToken);
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
}

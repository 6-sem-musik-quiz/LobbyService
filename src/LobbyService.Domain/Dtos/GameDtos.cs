namespace LobbyService.Domain.Dtos;

// Body sent to GameService POST /api/games when the host starts the game.
public record CreateGameRequest(
    Guid LobbyId,
    int CategoryId,
    string CategoryName,
    int Rounds,
    int AnswerTimeSeconds,
    IReadOnlyList<GamePlayerDto> Players);

public record GamePlayerDto(int UserId, string UserName);

// The part of GameService's response that LobbyService needs.
public record CreateGameResponse(Guid GameId);

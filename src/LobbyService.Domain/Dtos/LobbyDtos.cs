using LobbyService.Domain.Entities;

namespace LobbyService.Domain.Dtos;

// Body of POST /api/lobbies.
public record CreateLobbyRequest(
    int CategoryId,
    int AnswerTimeSeconds,
    int Rounds,
    int MaxPlayers,
    bool IsPrivate);

public record LobbySettingsDto(
    int CategoryId,
    string CategoryName,
    int AnswerTimeSeconds,
    int Rounds,
    int MaxPlayers);

public record LobbyPlayerDto(int UserId, string UserName, bool IsReady);

// Code is null for public lobbies.
public record LobbyDto(
    Guid Id,
    string? Code,
    int HostUserId,
    bool IsPrivate,
    LobbySettingsDto Settings,
    IReadOnlyList<LobbyPlayerDto> Players,
    LobbyStatus Status)
{
    public static LobbyDto FromEntity(Lobby lobby) => new(
        lobby.Id,
        lobby.IsPrivate ? lobby.Code : null,
        lobby.HostUserId,
        lobby.IsPrivate,
        new LobbySettingsDto(
            lobby.Settings.CategoryId,
            lobby.Settings.CategoryName,
            lobby.Settings.AnswerTimeSeconds,
            lobby.Settings.Rounds,
            lobby.Settings.MaxPlayers),
        lobby.Players.Select(p => new LobbyPlayerDto(p.UserId, p.UserName, p.IsReady)).ToList(),
        lobby.Status);
}

// One row in GET /api/lobbies. The host is always Players[0].
public record LobbySummaryDto(
    Guid Id,
    string HostUserName,
    string CategoryName,
    int PlayerCount,
    int MaxPlayers,
    int Rounds,
    int AnswerTimeSeconds)
{
    public static LobbySummaryDto FromEntity(Lobby lobby) => new(
        lobby.Id,
        lobby.Players.FirstOrDefault()?.UserName ?? string.Empty,
        lobby.Settings.CategoryName,
        lobby.Players.Count,
        lobby.Settings.MaxPlayers,
        lobby.Settings.Rounds,
        lobby.Settings.AnswerTimeSeconds);
}

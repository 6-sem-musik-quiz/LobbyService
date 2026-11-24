using LobbyService.Domain.Dtos;
using LobbyService.Domain.Entities;
using LobbyService.Domain.Exceptions;
using LobbyService.Repository.Interfaces;
using LobbyService.Service.Hubs;
using LobbyService.Service.Interfaces;
using LobbyService.Service.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace LobbyService.Service.Tests;

public class LobbyManagerTests
{
    private const int UserId = 42;
    private const string UserName = "alice";
    private const string Token = "token";

    private readonly ILobbyCacheRepository _repository = Substitute.For<ILobbyCacheRepository>();
    private readonly IMusicServiceClient _musicService = Substitute.For<IMusicServiceClient>();
    private readonly ILobbyCodeGenerator _codeGenerator = Substitute.For<ILobbyCodeGenerator>();
    private readonly IGameServiceClient _gameService = Substitute.For<IGameServiceClient>();
    private readonly IHubContext<LobbyHub, ILobbyClient> _hubContext = Substitute.For<IHubContext<LobbyHub, ILobbyClient>>();
    private readonly IHubClients<ILobbyClient> _hubClients = Substitute.For<IHubClients<ILobbyClient>>();
    private readonly ILobbyClient _group = Substitute.For<ILobbyClient>();
    private readonly LobbyManager _sut;

    public LobbyManagerTests()
    {
        _repository.GetLobbyIdForUserAsync(Arg.Any<int>()).Returns((Guid?)null);
        _repository.TryCreateAsync(Arg.Any<Lobby>()).Returns(CreateLobbyResult.Created);
        _musicService.GetPlaylistAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PlaylistDto(1, "90'er hits", 50));
        _codeGenerator.Generate().Returns("ABC234");
        _hubContext.Clients.Returns(_hubClients);
        _hubClients.Group(Arg.Any<string>()).Returns(_group);

        _sut = new LobbyManager(_repository, _musicService, _gameService, _codeGenerator, TimeProvider.System,
            _hubContext, NullLogger<LobbyManager>.Instance);
    }

    private static CreateLobbyRequest ValidRequest(bool isPrivate = false) =>
        new(CategoryId: 7, AnswerTimeSeconds: 30, Rounds: 10, MaxPlayers: 6, IsPrivate: isPrivate);

    [Fact]
    public async Task CreateAsync_ValidRequest_CreatesLobbyWithHostAsFirstPlayer()
    {
        var result = await _sut.CreateAsync(ValidRequest(), UserId, UserName, Token);

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Null(result.Code);
        Assert.Equal(UserId, result.HostUserId);
        Assert.False(result.IsPrivate);
        Assert.Equal(LobbyStatus.Waiting, result.Status);
        Assert.Equal(new LobbySettingsDto(7, "90'er hits", 30, 10, 6), result.Settings);
        var player = Assert.Single(result.Players);
        Assert.Equal(new LobbyPlayerDto(UserId, UserName, false), player);

        await _musicService.Received(1).GetPlaylistAsync(7, Token, Arg.Any<CancellationToken>());
        await _repository.Received(1).TryCreateAsync(Arg.Is<Lobby>(l =>
            l.HostUserId == UserId && l.Code == null && l.Status == LobbyStatus.Waiting && l.Players.Count == 1));
        _codeGenerator.DidNotReceive().Generate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(90)]
    public async Task CreateAsync_InvalidAnswerTime_ThrowsValidation(int answerTime)
    {
        var request = ValidRequest() with { AnswerTimeSeconds = answerTime };

        var ex = await Assert.ThrowsAsync<LobbyValidationException>(() => _sut.CreateAsync(request, UserId, UserName, Token));
        Assert.Contains("answerTimeSeconds", ex.Message);
        await _repository.DidNotReceive().TryCreateAsync(Arg.Any<Lobby>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(25)]
    public async Task CreateAsync_InvalidRounds_ThrowsValidation(int rounds)
    {
        var request = ValidRequest() with { Rounds = rounds };

        var ex = await Assert.ThrowsAsync<LobbyValidationException>(() => _sut.CreateAsync(request, UserId, UserName, Token));
        Assert.Contains("rounds", ex.Message);
        await _repository.DidNotReceive().TryCreateAsync(Arg.Any<Lobby>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public async Task CreateAsync_InvalidMaxPlayers_ThrowsValidation(int maxPlayers)
    {
        var request = ValidRequest() with { MaxPlayers = maxPlayers };

        var ex = await Assert.ThrowsAsync<LobbyValidationException>(() => _sut.CreateAsync(request, UserId, UserName, Token));
        Assert.Contains("maxPlayers", ex.Message);
        await _repository.DidNotReceive().TryCreateAsync(Arg.Any<Lobby>());
    }

    [Theory]
    [InlineData(15, 5, 2)]
    [InlineData(60, 20, 8)]
    public async Task CreateAsync_BoundaryValues_AreAccepted(int answerTime, int rounds, int maxPlayers)
    {
        var request = new CreateLobbyRequest(7, answerTime, rounds, maxPlayers, false);

        var result = await _sut.CreateAsync(request, UserId, UserName, Token);

        Assert.Equal(maxPlayers, result.Settings.MaxPlayers);
    }

    [Fact]
    public async Task CreateAsync_TooFewSongs_ThrowsValidationWithSongCount()
    {
        // 10 rounds need 11 songs (start card + one per round).
        _musicService.GetPlaylistAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PlaylistDto(1, "Kort liste", 10));

        var ex = await Assert.ThrowsAsync<LobbyValidationException>(() => _sut.CreateAsync(ValidRequest(), UserId, UserName, Token));
        Assert.Contains("10 songs", ex.Message);
        Assert.Contains("11", ex.Message);
        await _repository.DidNotReceive().TryCreateAsync(Arg.Any<Lobby>());
    }

    [Fact]
    public async Task CreateAsync_ExactlyRoundsPlusOneSongs_IsAccepted()
    {
        _musicService.GetPlaylistAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PlaylistDto(1, "Lige nok", 11));

        var result = await _sut.CreateAsync(ValidRequest(), UserId, UserName, Token);

        Assert.Equal("Lige nok", result.Settings.CategoryName);
    }

    [Fact]
    public async Task CreateAsync_UnknownPlaylist_ThrowsPlaylistNotFound()
    {
        _musicService.GetPlaylistAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PlaylistDto?)null);

        var ex = await Assert.ThrowsAsync<PlaylistNotFoundException>(() => _sut.CreateAsync(ValidRequest(), UserId, UserName, Token));
        Assert.Equal(7, ex.PlaylistId);
        await _repository.DidNotReceive().TryCreateAsync(Arg.Any<Lobby>());
    }

    [Fact]
    public async Task CreateAsync_PrivateLobby_GetsCode()
    {
        var result = await _sut.CreateAsync(ValidRequest(isPrivate: true), UserId, UserName, Token);

        Assert.True(result.IsPrivate);
        Assert.Equal("ABC234", result.Code);
        await _repository.Received(1).TryCreateAsync(Arg.Is<Lobby>(l => l.Code == "ABC234" && l.IsPrivate));
    }

    [Fact]
    public async Task CreateAsync_PrivateLobby_RetriesWhenCodeIsTaken()
    {
        _codeGenerator.Generate().Returns("TAKEN2", "FREE34");
        _repository.TryCreateAsync(Arg.Is<Lobby>(l => l.Code == "TAKEN2")).Returns(CreateLobbyResult.CodeTaken);
        _repository.TryCreateAsync(Arg.Is<Lobby>(l => l.Code == "FREE34")).Returns(CreateLobbyResult.Created);

        var result = await _sut.CreateAsync(ValidRequest(isPrivate: true), UserId, UserName, Token);

        Assert.Equal("FREE34", result.Code);
        _codeGenerator.Received(2).Generate();
    }

    [Fact]
    public async Task CreateAsync_PrivateLobby_GivesUpAfterMaxAttempts()
    {
        _repository.TryCreateAsync(Arg.Any<Lobby>()).Returns(CreateLobbyResult.CodeTaken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.CreateAsync(ValidRequest(isPrivate: true), UserId, UserName, Token));
        _codeGenerator.Received(LobbyManager.MaxCodeAttempts).Generate();
    }

    [Fact]
    public async Task CreateAsync_UserAlreadyInLobby_ThrowsConflict()
    {
        var current = MakeLobby(0, userIds: [UserId]);
        _repository.GetLobbyIdForUserAsync(UserId).Returns(current.Id);
        _repository.GetAsync(current.Id).Returns(current);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.CreateAsync(ValidRequest(), UserId, UserName, Token));
        await _musicService.DidNotReceive().GetPlaylistAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().TryCreateAsync(Arg.Any<Lobby>());
    }

    [Fact]
    public async Task CreateAsync_UserJoinsAnotherLobbyConcurrently_ThrowsConflict()
    {
        // The pre-check passed, but the atomic write found an existing user-lobby key.
        _repository.TryCreateAsync(Arg.Any<Lobby>()).Returns(CreateLobbyResult.UserAlreadyInLobby);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.CreateAsync(ValidRequest(), UserId, UserName, Token));
    }

    private static readonly DateTimeOffset BaseTime = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static Lobby MakeLobby(int minutesAfterBase, bool isPrivate = false, LobbyStatus status = LobbyStatus.Waiting,
        int players = 1, int maxPlayers = 4, params int[] userIds) => new()
    {
        Id = Guid.NewGuid(),
        HostUserId = userIds.Length > 0 ? userIds[0] : 1,
        IsPrivate = isPrivate,
        Code = isPrivate ? "PRIV23" : null,
        Settings = new LobbySettings
        {
            CategoryId = 3, CategoryName = "Rock", AnswerTimeSeconds = 45, Rounds = 15, MaxPlayers = maxPlayers
        },
        Players = (userIds.Length > 0 ? userIds : Enumerable.Range(1, players).ToArray())
            .Select(id => new LobbyPlayer { UserId = id, UserName = $"user{id}" }).ToList(),
        Status = status,
        CreatedAt = BaseTime.AddMinutes(minutesAfterBase)
    };

    [Fact]
    public async Task GetOpenLobbiesAsync_ExcludesPrivateAndStartedLobbies()
    {
        var open = MakeLobby(0);
        _repository.GetPublicWaitingAsync().Returns(
            [open, MakeLobby(1, isPrivate: true), MakeLobby(2, status: LobbyStatus.InGame)]);

        var result = await _sut.GetOpenLobbiesAsync();

        Assert.Equal(open.Id, Assert.Single(result).Id);
    }

    [Fact]
    public async Task GetOpenLobbiesAsync_NoLobbies_ReturnsEmptyList()
    {
        _repository.GetPublicWaitingAsync().Returns([]);

        var result = await _sut.GetOpenLobbiesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetOpenLobbiesAsync_IncludesFullLobbies()
    {
        var full = MakeLobby(0, players: 4, maxPlayers: 4);
        _repository.GetPublicWaitingAsync().Returns([full]);

        var row = Assert.Single(await _sut.GetOpenLobbiesAsync());

        Assert.Equal(new LobbySummaryDto(full.Id, "user1", "Rock", 4, 4, 15, 45), row);
    }

    [Fact]
    public async Task GetOpenLobbiesAsync_SortsNewestFirst()
    {
        var oldest = MakeLobby(0);
        var newest = MakeLobby(20);
        var middle = MakeLobby(10);
        _repository.GetPublicWaitingAsync().Returns([oldest, newest, middle]);

        var result = await _sut.GetOpenLobbiesAsync();

        Assert.Equal([newest.Id, middle.Id, oldest.Id], result.Select(r => r.Id));
    }

    [Fact]
    public async Task GetByIdAsync_UnknownLobby_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((Lobby?)null);

        var ex = await Assert.ThrowsAsync<LobbyNotFoundException>(() => _sut.GetByIdAsync(id, UserId));
        Assert.Equal(id, ex.LobbyId);
    }

    [Fact]
    public async Task GetByIdAsync_PrivateLobbyUserIsNotIn_ThrowsNotFound()
    {
        var lobby = MakeLobby(0, isPrivate: true, userIds: [7, 8]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await Assert.ThrowsAsync<LobbyNotFoundException>(() => _sut.GetByIdAsync(lobby.Id, UserId));
    }

    [Fact]
    public async Task GetByIdAsync_PrivateLobbyUserIsIn_ReturnsDetailsWithCode()
    {
        var lobby = MakeLobby(0, isPrivate: true, userIds: [7, UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        var result = await _sut.GetByIdAsync(lobby.Id, UserId);

        Assert.Equal("PRIV23", result.Code);
        Assert.Equal(2, result.Players.Count);
    }

    [Fact]
    public async Task GetByIdAsync_PublicLobby_ReturnsDetailsToAnyone()
    {
        var lobby = MakeLobby(0, players: 2);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        var result = await _sut.GetByIdAsync(lobby.Id, UserId);

        Assert.Equal(lobby.Id, result.Id);
        Assert.Null(result.Code);
        Assert.Equal(["user1", "user2"], result.Players.Select(p => p.UserName));
    }

    // ---- Join ----

    private void SetupJoinUpdated() =>
        _repository.TryJoinAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>(), Arg.Any<int>()).Returns(LobbyUpdateResult.Updated);

    [Fact]
    public async Task JoinAsync_PublicLobby_AddsPlayerLastAndNotReady()
    {
        var lobby = MakeLobby(0, userIds: [7, 8]);
        lobby.Players[0].IsReady = true;
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupJoinUpdated();

        var result = await _sut.JoinAsync(lobby.Id, UserId, UserName);

        Assert.Equal([7, 8, UserId], result.Players.Select(p => p.UserId));
        Assert.Equal(new LobbyPlayerDto(UserId, UserName, false), result.Players[^1]);
        Assert.Equal(7, result.HostUserId);
        await _repository.Received(1).TryJoinAsync(
            Arg.Is<Lobby>(b => b.Players.Count == 2 && b.Players[0].IsReady),
            Arg.Is<Lobby>(a => a.Players.Count == 3 && a.Players[2].UserId == UserId && !a.Players[2].IsReady),
            UserId);
    }

    [Fact]
    public async Task JoinAsync_UnknownLobby_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((Lobby?)null);

        await Assert.ThrowsAsync<LobbyNotFoundException>(() => _sut.JoinAsync(id, UserId, UserName));
    }

    [Fact]
    public async Task JoinAsync_PrivateLobbyById_ThrowsNotFound()
    {
        var lobby = MakeLobby(0, isPrivate: true, userIds: [7]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await Assert.ThrowsAsync<LobbyNotFoundException>(() => _sut.JoinAsync(lobby.Id, UserId, UserName));
        await _repository.DidNotReceiveWithAnyArgs().TryJoinAsync(default!, default!, default);
    }

    [Fact]
    public async Task JoinAsync_FullLobby_ThrowsConflict()
    {
        var lobby = MakeLobby(0, players: 4, maxPlayers: 4);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        var ex = await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.JoinAsync(lobby.Id, UserId, UserName));
        Assert.Contains("full", ex.Message);
        await _repository.DidNotReceiveWithAnyArgs().TryJoinAsync(default!, default!, default);
    }

    [Fact]
    public async Task JoinAsync_GameStarted_ThrowsConflict()
    {
        var lobby = MakeLobby(0, status: LobbyStatus.InGame, userIds: [7]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        var ex = await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.JoinAsync(lobby.Id, UserId, UserName));
        Assert.Contains("started", ex.Message);
    }

    [Fact]
    public async Task JoinAsync_UserInAnotherLobby_ThrowsConflict()
    {
        var lobby = MakeLobby(0, userIds: [7]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        var other = MakeLobby(0, userIds: [8, UserId]);
        _repository.GetAsync(other.Id).Returns(other);
        _repository.GetLobbyIdForUserAsync(UserId).Returns(other.Id);

        var ex = await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.JoinAsync(lobby.Id, UserId, UserName));
        Assert.Contains("another lobby", ex.Message);
        await _repository.DidNotReceiveWithAnyArgs().TryJoinAsync(default!, default!, default);
    }

    [Fact]
    public async Task JoinAsync_UserJoinsAnotherLobbyConcurrently_ThrowsConflict()
    {
        var lobby = MakeLobby(0, userIds: [7]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        _repository.TryJoinAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>(), UserId).Returns(LobbyUpdateResult.UserInOtherLobby);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.JoinAsync(lobby.Id, UserId, UserName));
    }

    [Fact]
    public async Task JoinAsync_AlreadyInThisLobby_ReturnsLobbyUnchanged()
    {
        var lobby = MakeLobby(0, players: 4, maxPlayers: 4, userIds: [7, UserId, 8, 9]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        _repository.GetLobbyIdForUserAsync(UserId).Returns(lobby.Id);

        var result = await _sut.JoinAsync(lobby.Id, UserId, UserName);

        Assert.Equal(4, result.Players.Count);
        await _repository.DidNotReceiveWithAnyArgs().TryJoinAsync(default!, default!, default);
    }

    [Fact]
    public async Task JoinAsync_LobbyChangedConcurrently_RetriesWithFreshData()
    {
        var first = MakeLobby(0, maxPlayers: 3, userIds: [7]);
        var fresh = MakeLobby(0, maxPlayers: 3, userIds: [7, 8]);
        fresh.Id = first.Id;
        _repository.GetAsync(first.Id).Returns(first, fresh);
        _repository.TryJoinAsync(Arg.Is<Lobby>(l => l.Players.Count == 1), Arg.Any<Lobby>(), UserId)
            .Returns(LobbyUpdateResult.Stale);
        _repository.TryJoinAsync(Arg.Is<Lobby>(l => l.Players.Count == 2), Arg.Any<Lobby>(), UserId)
            .Returns(LobbyUpdateResult.Updated);

        var result = await _sut.JoinAsync(first.Id, UserId, UserName);

        Assert.Equal([7, 8, UserId], result.Players.Select(p => p.UserId));
    }

    [Fact]
    public async Task JoinAsync_LastSeatTakenConcurrently_ThrowsFullOnRetry()
    {
        var first = MakeLobby(0, maxPlayers: 2, userIds: [7]);
        var fresh = MakeLobby(0, maxPlayers: 2, userIds: [7, 8]);
        fresh.Id = first.Id;
        _repository.GetAsync(first.Id).Returns(first, fresh);
        _repository.TryJoinAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>(), UserId).Returns(LobbyUpdateResult.Stale);

        var ex = await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.JoinAsync(first.Id, UserId, UserName));
        Assert.Contains("full", ex.Message);
        await _repository.Received(1).TryJoinAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>(), UserId);
    }

    [Fact]
    public async Task JoinAsync_AlwaysStale_GivesUpAfterMaxAttempts()
    {
        var lobby = MakeLobby(0, userIds: [7]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        _repository.TryJoinAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>(), UserId).Returns(LobbyUpdateResult.Stale);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.JoinAsync(lobby.Id, UserId, UserName));
        await _repository.Received(LobbyManager.MaxUpdateAttempts)
            .TryJoinAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>(), UserId);
    }

    [Theory]
    [InlineData("PRIV23")]
    [InlineData("priv23")]
    [InlineData("  Priv23 ")]
    public async Task JoinByCodeAsync_MatchesCaseInsensitively(string code)
    {
        var lobby = MakeLobby(0, isPrivate: true, userIds: [7]);
        _repository.GetLobbyIdByCodeAsync("PRIV23").Returns(lobby.Id);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupJoinUpdated();

        var result = await _sut.JoinByCodeAsync(code, UserId, UserName);

        Assert.Equal(lobby.Id, result.Id);
        Assert.Equal("PRIV23", result.Code);
        Assert.Equal([7, UserId], result.Players.Select(p => p.UserId));
    }

    [Theory]
    [InlineData("NOPE23")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task JoinByCodeAsync_UnknownCode_ThrowsCodeNotFound(string code)
    {
        _repository.GetLobbyIdByCodeAsync(Arg.Any<string>()).Returns((Guid?)null);

        await Assert.ThrowsAsync<LobbyCodeNotFoundException>(() => _sut.JoinByCodeAsync(code, UserId, UserName));
    }

    [Fact]
    public async Task JoinByCodeAsync_CodeOfDeletedLobby_ThrowsCodeNotFound()
    {
        var id = Guid.NewGuid();
        _repository.GetLobbyIdByCodeAsync("PRIV23").Returns(id);
        _repository.GetAsync(id).Returns((Lobby?)null);

        await Assert.ThrowsAsync<LobbyCodeNotFoundException>(() => _sut.JoinByCodeAsync("PRIV23", UserId, UserName));
    }

    [Fact]
    public async Task JoinByCodeAsync_FullLobby_ThrowsConflict()
    {
        var lobby = MakeLobby(0, isPrivate: true, maxPlayers: 2, userIds: [7, 8]);
        _repository.GetLobbyIdByCodeAsync("PRIV23").Returns(lobby.Id);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.JoinByCodeAsync("PRIV23", UserId, UserName));
    }

    // ---- Leave ----

    private void SetupLeaveUpdated() =>
        _repository.TryLeaveAsync(Arg.Any<Lobby>(), Arg.Any<Lobby?>(), Arg.Any<int>()).Returns(LobbyUpdateResult.Updated);

    [Fact]
    public async Task LeaveAsync_NonHostLeaves_RemovesPlayerAndKeepsHost()
    {
        var lobby = MakeLobby(0, userIds: [7, UserId, 8]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupLeaveUpdated();

        await _sut.LeaveAsync(lobby.Id, UserId);

        await _repository.Received(1).TryLeaveAsync(lobby,
            Arg.Is<Lobby?>(a => a!.HostUserId == 7 && a.Players.Select(p => p.UserId).SequenceEqual(new[] { 7, 8 })),
            UserId);
    }

    [Fact]
    public async Task LeaveAsync_HostLeaves_LongestStayingPlayerBecomesHost()
    {
        var lobby = MakeLobby(0, userIds: [UserId, 8, 9]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupLeaveUpdated();

        await _sut.LeaveAsync(lobby.Id, UserId);

        await _repository.Received(1).TryLeaveAsync(lobby,
            Arg.Is<Lobby?>(a => a!.HostUserId == 8 && a.Players.Select(p => p.UserId).SequenceEqual(new[] { 8, 9 })),
            UserId);
    }

    [Fact]
    public async Task LeaveAsync_LastPlayerLeaves_DeletesLobby()
    {
        var lobby = MakeLobby(0, isPrivate: true, userIds: [UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupLeaveUpdated();

        await _sut.LeaveAsync(lobby.Id, UserId);

        await _repository.Received(1).TryLeaveAsync(lobby, null, UserId);
    }

    [Fact]
    public async Task LeaveAsync_GameRunning_ThrowsConflict()
    {
        var lobby = MakeLobby(0, status: LobbyStatus.InGame, userIds: [7, UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.LeaveAsync(lobby.Id, UserId));
        await _repository.DidNotReceiveWithAnyArgs().TryLeaveAsync(default!, default, default);
    }

    [Fact]
    public async Task LeaveAsync_UserNotInLobby_ThrowsNotFound()
    {
        var lobby = MakeLobby(0, userIds: [7, 8]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await Assert.ThrowsAsync<LobbyNotFoundException>(() => _sut.LeaveAsync(lobby.Id, UserId));
    }

    [Fact]
    public async Task LeaveAsync_UnknownLobby_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((Lobby?)null);

        await Assert.ThrowsAsync<LobbyNotFoundException>(() => _sut.LeaveAsync(id, UserId));
    }

    [Fact]
    public async Task LeaveAsync_LobbyChangedConcurrently_Retries()
    {
        var first = MakeLobby(0, userIds: [UserId, 8]);
        var fresh = MakeLobby(0, userIds: [UserId, 8, 9]);
        fresh.Id = first.Id;
        _repository.GetAsync(first.Id).Returns(first, fresh);
        _repository.TryLeaveAsync(first, Arg.Any<Lobby?>(), UserId).Returns(LobbyUpdateResult.Stale);
        _repository.TryLeaveAsync(fresh, Arg.Any<Lobby?>(), UserId).Returns(LobbyUpdateResult.Updated);

        await _sut.LeaveAsync(first.Id, UserId);

        await _repository.Received(1).TryLeaveAsync(fresh,
            Arg.Is<Lobby?>(a => a!.HostUserId == 8 && a.Players.Count == 2), UserId);
    }

    // ---- Ready ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetReadyAsync_ChangesOnlyCallersFlag(bool ready)
    {
        var lobby = MakeLobby(0, userIds: [7, UserId]);
        lobby.Players[1].IsReady = !ready;
        _repository.GetAsync(lobby.Id).Returns(lobby);
        _repository.TryUpdateAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>()).Returns(LobbyUpdateResult.Updated);

        var result = await _sut.SetReadyAsync(lobby.Id, UserId, ready);

        Assert.Equal(ready, result.Players[1].IsReady);
        Assert.False(result.Players[0].IsReady);
        await _repository.Received(1).TryUpdateAsync(lobby,
            Arg.Is<Lobby>(a => a.Players[1].IsReady == ready && !a.Players[0].IsReady));
    }

    [Fact]
    public async Task SetReadyAsync_AlreadyInRequestedState_DoesNotWrite()
    {
        var lobby = MakeLobby(0, userIds: [7, UserId]);
        lobby.Players[1].IsReady = true;
        _repository.GetAsync(lobby.Id).Returns(lobby);

        var result = await _sut.SetReadyAsync(lobby.Id, UserId, true);

        Assert.True(result.Players[1].IsReady);
        await _repository.DidNotReceiveWithAnyArgs().TryUpdateAsync(default!, default!);
    }

    [Fact]
    public async Task SetReadyAsync_UserNotInLobby_ThrowsNotFound()
    {
        var lobby = MakeLobby(0, userIds: [7, 8]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await Assert.ThrowsAsync<LobbyNotFoundException>(() => _sut.SetReadyAsync(lobby.Id, UserId, true));
    }

    [Fact]
    public async Task SetReadyAsync_UnknownLobby_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((Lobby?)null);

        await Assert.ThrowsAsync<LobbyNotFoundException>(() => _sut.SetReadyAsync(id, UserId, true));
    }

    [Fact]
    public async Task SetReadyAsync_GameStarted_ThrowsConflict()
    {
        var lobby = MakeLobby(0, status: LobbyStatus.InGame, userIds: [7, UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.SetReadyAsync(lobby.Id, UserId, true));
        await _repository.DidNotReceiveWithAnyArgs().TryUpdateAsync(default!, default!);
    }

    [Fact]
    public async Task SetReadyAsync_LobbyChangedConcurrently_RetriesWithFreshData()
    {
        var first = MakeLobby(0, userIds: [7, UserId]);
        var fresh = MakeLobby(0, userIds: [7, UserId, 8]);
        fresh.Id = first.Id;
        _repository.GetAsync(first.Id).Returns(first, fresh);
        _repository.TryUpdateAsync(first, Arg.Any<Lobby>()).Returns(LobbyUpdateResult.Stale);
        _repository.TryUpdateAsync(fresh, Arg.Any<Lobby>()).Returns(LobbyUpdateResult.Updated);

        var result = await _sut.SetReadyAsync(first.Id, UserId, true);

        Assert.Equal(3, result.Players.Count);
        Assert.True(result.Players[1].IsReady);
    }

    // ---- Start ----

    private static readonly Guid GameId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private Lobby ReadyLobby(params int[] userIds)
    {
        var lobby = MakeLobby(0, isPrivate: true, userIds: userIds.Length > 0 ? userIds : [UserId, 7, 8]);
        lobby.Players.ForEach(p => p.IsReady = true);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        return lobby;
    }

    private void SetupStartSucceeds()
    {
        _gameService.CreateGameAsync(Arg.Any<CreateGameRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GameId);
        _repository.TryStartAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>()).Returns(LobbyUpdateResult.Updated);
    }

    [Fact]
    public async Task StartAsync_AllReady_CallsGameServiceAndMarksLobbyInGame()
    {
        var lobby = ReadyLobby();
        SetupStartSucceeds();

        var result = await _sut.StartAsync(lobby.Id, UserId, Token);

        Assert.Equal(GameId, result.GameId);
        await _gameService.Received(1).CreateGameAsync(
            Arg.Is<CreateGameRequest>(r =>
                r.LobbyId == lobby.Id && r.CategoryId == 3 && r.CategoryName == "Rock" && r.Rounds == 15 &&
                r.AnswerTimeSeconds == 45 &&
                r.Players.SequenceEqual(new[]
                {
                    new GamePlayerDto(UserId, $"user{UserId}"), new GamePlayerDto(7, "user7"), new GamePlayerDto(8, "user8")
                })),
            Token, Arg.Any<CancellationToken>());
        await _repository.Received(1).TryStartAsync(lobby,
            Arg.Is<Lobby>(a => a.Status == LobbyStatus.InGame && a.GameId == GameId && a.Players.Count == 3));
    }

    [Fact]
    public async Task StartAsync_NotHost_ThrowsForbidden()
    {
        var lobby = ReadyLobby(7, UserId);

        await Assert.ThrowsAsync<LobbyForbiddenException>(() => _sut.StartAsync(lobby.Id, UserId, Token));
        await AssertNothingStarted();
    }

    [Fact]
    public async Task StartAsync_TooFewPlayers_ThrowsConflict()
    {
        var lobby = ReadyLobby(UserId);

        var ex = await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.StartAsync(lobby.Id, UserId, Token));
        Assert.Contains("2 players", ex.Message);
        await AssertNothingStarted();
    }

    [Fact]
    public async Task StartAsync_NotAllReady_ThrowsConflict()
    {
        var lobby = ReadyLobby();
        lobby.Players[2].IsReady = false;

        var ex = await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.StartAsync(lobby.Id, UserId, Token));
        Assert.Contains("ready", ex.Message);
        await AssertNothingStarted();
    }

    [Fact]
    public async Task StartAsync_AlreadyStarted_ThrowsConflict()
    {
        var lobby = ReadyLobby();
        lobby.Status = LobbyStatus.InGame;

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.StartAsync(lobby.Id, UserId, Token));
        await AssertNothingStarted();
    }

    [Fact]
    public async Task StartAsync_UserNotInLobby_ThrowsNotFound()
    {
        var lobby = ReadyLobby(7, 8);

        await Assert.ThrowsAsync<LobbyNotFoundException>(() => _sut.StartAsync(lobby.Id, UserId, Token));
        await AssertNothingStarted();
    }

    [Fact]
    public async Task StartAsync_GameServiceFails_ThrowsAndLeavesLobbyWaiting()
    {
        var lobby = ReadyLobby();
        _gameService.CreateGameAsync(Arg.Any<CreateGameRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Guid>(_ => throw new GameServiceUnavailableException("down"));

        await Assert.ThrowsAsync<GameServiceUnavailableException>(() => _sut.StartAsync(lobby.Id, UserId, Token));
        await _repository.DidNotReceiveWithAnyArgs().TryStartAsync(default!, default!);
        await _repository.DidNotReceiveWithAnyArgs().TryUpdateAsync(default!, default!);
        Assert.Equal(LobbyStatus.Waiting, lobby.Status);
    }

    [Fact]
    public async Task StartAsync_LobbyChangedButSamePlayers_RetriesWithoutCreatingSecondGame()
    {
        var first = ReadyLobby();
        var fresh = ReadyLobby();
        fresh.Id = first.Id;
        fresh.Settings.MaxPlayers = 5; // changed JSON, same players
        _repository.GetAsync(first.Id).Returns(first, fresh);
        SetupStartSucceeds();
        _repository.TryStartAsync(first, Arg.Any<Lobby>()).Returns(LobbyUpdateResult.Stale);

        var result = await _sut.StartAsync(first.Id, UserId, Token);

        Assert.Equal(GameId, result.GameId);
        await _gameService.Received(1).CreateGameAsync(Arg.Any<CreateGameRequest>(), Token, Arg.Any<CancellationToken>());
        await _repository.Received(1).TryStartAsync(fresh, Arg.Any<Lobby>());
    }

    [Fact]
    public async Task StartAsync_PlayerJoinedWhileStarting_ThrowsConflict()
    {
        var first = ReadyLobby();
        var fresh = ReadyLobby(UserId, 7, 8, 9);
        fresh.Id = first.Id;
        _repository.GetAsync(first.Id).Returns(first, fresh);
        SetupStartSucceeds();
        _repository.TryStartAsync(first, Arg.Any<Lobby>()).Returns(LobbyUpdateResult.Stale);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.StartAsync(first.Id, UserId, Token));
        await _repository.Received(1).TryStartAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>());
    }

    private async Task AssertNothingStarted()
    {
        await _gameService.DidNotReceiveWithAnyArgs().CreateGameAsync(default!, default!, default);
        await _repository.DidNotReceiveWithAnyArgs().TryStartAsync(default!, default!);
    }

    // ---- SignalR notifications ----

    private void AssertSentOnlyToGroupOf(Guid lobbyId)
    {
        _hubClients.Received(1).Group($"lobby:{lobbyId}");
        _hubClients.DidNotReceive().Group(Arg.Is<string>(g => g != $"lobby:{lobbyId}"));
        Assert.Single(_group.ReceivedCalls());
    }

    private void AssertNothingSent()
    {
        Assert.Empty(_hubClients.ReceivedCalls());
        Assert.Empty(_group.ReceivedCalls());
    }

    [Fact]
    public void GroupName_IsLobbyColonId()
    {
        var id = Guid.NewGuid();

        Assert.Equal($"lobby:{id}", LobbyHub.GroupName(id));
    }

    [Fact]
    public async Task JoinAsync_Success_SendsLobbyUpdatedToLobbyGroup()
    {
        var lobby = MakeLobby(0, userIds: [7]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupJoinUpdated();

        var result = await _sut.JoinAsync(lobby.Id, UserId, UserName);

        AssertSentOnlyToGroupOf(lobby.Id);
        await _group.Received(1).LobbyUpdated(result);
        await _group.Received(1).LobbyUpdated(Arg.Is<LobbyDto>(d => d.Players.Count == 2 && d.Players[1].UserId == UserId));
    }

    [Fact]
    public async Task JoinByCodeAsync_Success_SendsLobbyUpdatedToLobbyGroup()
    {
        var lobby = MakeLobby(0, isPrivate: true, userIds: [7]);
        _repository.GetLobbyIdByCodeAsync("PRIV23").Returns(lobby.Id);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupJoinUpdated();

        var result = await _sut.JoinByCodeAsync("priv23", UserId, UserName);

        AssertSentOnlyToGroupOf(lobby.Id);
        await _group.Received(1).LobbyUpdated(result);
    }

    [Fact]
    public async Task JoinAsync_AlreadyInLobby_SendsNothing()
    {
        var lobby = MakeLobby(0, userIds: [7, UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await _sut.JoinAsync(lobby.Id, UserId, UserName);

        AssertNothingSent();
    }

    [Fact]
    public async Task JoinAsync_Fails_SendsNothing()
    {
        var lobby = MakeLobby(0, maxPlayers: 2, userIds: [7, 8]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.JoinAsync(lobby.Id, UserId, UserName));

        AssertNothingSent();
    }

    [Fact]
    public async Task JoinAsync_StaleThenUpdated_SendsOnlyOnce()
    {
        var first = MakeLobby(0, userIds: [7]);
        var fresh = MakeLobby(0, userIds: [7, 8]);
        fresh.Id = first.Id;
        _repository.GetAsync(first.Id).Returns(first, fresh);
        _repository.TryJoinAsync(first, Arg.Any<Lobby>(), UserId).Returns(LobbyUpdateResult.Stale);
        _repository.TryJoinAsync(fresh, Arg.Any<Lobby>(), UserId).Returns(LobbyUpdateResult.Updated);

        await _sut.JoinAsync(first.Id, UserId, UserName);

        AssertSentOnlyToGroupOf(first.Id);
        await _group.Received(1).LobbyUpdated(Arg.Is<LobbyDto>(d => d.Players.Count == 3));
    }

    [Fact]
    public async Task LeaveAsync_NonHostLeaves_SendsLobbyUpdatedWithoutPlayer()
    {
        var lobby = MakeLobby(0, userIds: [7, UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupLeaveUpdated();

        await _sut.LeaveAsync(lobby.Id, UserId);

        AssertSentOnlyToGroupOf(lobby.Id);
        await _group.Received(1).LobbyUpdated(Arg.Is<LobbyDto>(d =>
            d.HostUserId == 7 && d.Players.Select(p => p.UserId).SequenceEqual(new[] { 7 })));
    }

    [Fact]
    public async Task LeaveAsync_HostLeaves_SendsLobbyUpdatedWithNewHost()
    {
        var lobby = MakeLobby(0, userIds: [UserId, 8, 9]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupLeaveUpdated();

        await _sut.LeaveAsync(lobby.Id, UserId);

        AssertSentOnlyToGroupOf(lobby.Id);
        await _group.Received(1).LobbyUpdated(Arg.Is<LobbyDto>(d => d.HostUserId == 8 && d.Players.Count == 2));
    }

    [Fact]
    public async Task LeaveAsync_LastPlayerLeaves_SendsLobbyClosed()
    {
        var lobby = MakeLobby(0, userIds: [UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        SetupLeaveUpdated();

        await _sut.LeaveAsync(lobby.Id, UserId);

        AssertSentOnlyToGroupOf(lobby.Id);
        await _group.Received(1).LobbyClosed();
    }

    [Fact]
    public async Task LeaveAsync_Fails_SendsNothing()
    {
        var lobby = MakeLobby(0, status: LobbyStatus.InGame, userIds: [7, UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await Assert.ThrowsAsync<LobbyConflictException>(() => _sut.LeaveAsync(lobby.Id, UserId));

        AssertNothingSent();
    }

    [Fact]
    public async Task SetReadyAsync_Changed_SendsLobbyUpdated()
    {
        var lobby = MakeLobby(0, userIds: [7, UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        _repository.TryUpdateAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>()).Returns(LobbyUpdateResult.Updated);

        var result = await _sut.SetReadyAsync(lobby.Id, UserId, true);

        AssertSentOnlyToGroupOf(lobby.Id);
        await _group.Received(1).LobbyUpdated(result);
        await _group.Received(1).LobbyUpdated(Arg.Is<LobbyDto>(d => d.Players[1].IsReady));
    }

    [Fact]
    public async Task SetReadyAsync_Unchanged_SendsNothing()
    {
        var lobby = MakeLobby(0, userIds: [7, UserId]);
        lobby.Players[1].IsReady = true;
        _repository.GetAsync(lobby.Id).Returns(lobby);

        await _sut.SetReadyAsync(lobby.Id, UserId, true);

        AssertNothingSent();
    }

    [Fact]
    public async Task StartAsync_Success_SendsGameStartedWithGameId()
    {
        var lobby = ReadyLobby();
        SetupStartSucceeds();

        await _sut.StartAsync(lobby.Id, UserId, Token);

        AssertSentOnlyToGroupOf(lobby.Id);
        await _group.Received(1).GameStarted(new StartGameResponse(GameId));
    }

    [Fact]
    public async Task StartAsync_GameServiceFails_SendsNothing()
    {
        var lobby = ReadyLobby();
        _gameService.CreateGameAsync(Arg.Any<CreateGameRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GameServiceUnavailableException("down"));

        await Assert.ThrowsAsync<GameServiceUnavailableException>(() => _sut.StartAsync(lobby.Id, UserId, Token));

        AssertNothingSent();
    }

    [Fact]
    public async Task SendFails_ChangeIsStillReturned()
    {
        // The lobby is already stored, so a SignalR failure must not turn the request into an error.
        var lobby = MakeLobby(0, userIds: [7, UserId]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        _repository.TryUpdateAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>()).Returns(LobbyUpdateResult.Updated);
        _group.LobbyUpdated(Arg.Any<LobbyDto>()).ThrowsAsync(new InvalidOperationException("backplane down"));

        var result = await _sut.SetReadyAsync(lobby.Id, UserId, true);

        Assert.True(result.Players[1].IsReady);
    }

    // A user-lobby key is only trusted when its lobby exists, is waiting and still lists the user.
    // Otherwise it is stale: it is cleared and the user can create or join again.
    public static TheoryData<string> StaleReasons => ["gone", "notAPlayer", "started"];

    private Guid StaleUserLobby(string reason)
    {
        var lobby = reason switch
        {
            "notAPlayer" => MakeLobby(0, userIds: [7, 8]),
            "started" => MakeLobby(0, status: LobbyStatus.InGame, userIds: [7, UserId]),
            _ => null
        };
        var id = lobby?.Id ?? Guid.NewGuid();
        _repository.GetLobbyIdForUserAsync(UserId).Returns(id);
        _repository.GetAsync(id).Returns(lobby);
        return id;
    }

    [Theory]
    [MemberData(nameof(StaleReasons))]
    public async Task CreateAsync_StaleUserLobby_ClearsItAndCreates(string reason)
    {
        var staleId = StaleUserLobby(reason);

        var result = await _sut.CreateAsync(ValidRequest(), UserId, UserName, Token);

        Assert.Equal(UserId, result.HostUserId);
        await _repository.Received(1).ClearUserLobbyAsync(UserId, staleId);
        await _repository.Received(1).TryCreateAsync(Arg.Any<Lobby>());
    }

    [Theory]
    [MemberData(nameof(StaleReasons))]
    public async Task JoinAsync_StaleUserLobby_ClearsItAndJoins(string reason)
    {
        var staleId = StaleUserLobby(reason);
        var lobby = MakeLobby(0, userIds: [7]);
        _repository.GetAsync(lobby.Id).Returns(lobby);
        _repository.TryJoinAsync(Arg.Any<Lobby>(), Arg.Any<Lobby>(), UserId).Returns(LobbyUpdateResult.Updated);

        var result = await _sut.JoinAsync(lobby.Id, UserId, UserName);

        Assert.Contains(result.Players, p => p.UserId == UserId);
        await _repository.Received(1).ClearUserLobbyAsync(UserId, staleId);
    }

    [Fact]
    public async Task GetMyLobbyAsync_ReturnsTheLobbyTheUserIsIn()
    {
        var lobby = MakeLobby(0, userIds: [7, UserId]);
        _repository.GetLobbyIdForUserAsync(UserId).Returns(lobby.Id);
        _repository.GetAsync(lobby.Id).Returns(lobby);

        var result = await _sut.GetMyLobbyAsync(UserId);

        Assert.Equal(lobby.Id, result?.Id);
        await _repository.DidNotReceiveWithAnyArgs().ClearUserLobbyAsync(default, default);
    }

    [Fact]
    public async Task GetMyLobbyAsync_NotInALobby_ReturnsNull()
    {
        Assert.Null(await _sut.GetMyLobbyAsync(UserId));
        await _repository.DidNotReceiveWithAnyArgs().ClearUserLobbyAsync(default, default);
    }

    [Theory]
    [MemberData(nameof(StaleReasons))]
    public async Task GetMyLobbyAsync_StaleUserLobby_ClearsItAndReturnsNull(string reason)
    {
        var staleId = StaleUserLobby(reason);

        Assert.Null(await _sut.GetMyLobbyAsync(UserId));
        await _repository.Received(1).ClearUserLobbyAsync(UserId, staleId);
    }
}

public class LobbyCodeGeneratorTests
{
    [Fact]
    public void Generate_ReturnsSixCharsFromAllowedAlphabet()
    {
        var generator = new LobbyCodeGenerator();

        for (var i = 0; i < 1000; i++)
        {
            var code = generator.Generate();
            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.Contains(c, LobbyCodeGenerator.Alphabet));
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('1', code);
            Assert.DoesNotContain('I', code);
        }
    }
}

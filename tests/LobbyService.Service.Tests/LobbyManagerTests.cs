using LobbyService.Domain.Dtos;
using LobbyService.Domain.Entities;
using LobbyService.Domain.Exceptions;
using LobbyService.Repository.Interfaces;
using LobbyService.Service.Interfaces;
using LobbyService.Service.Services;
using NSubstitute;

namespace LobbyService.Service.Tests;

public class LobbyManagerTests
{
    private const int UserId = 42;
    private const string UserName = "alice";
    private const string Token = "token";

    private readonly ILobbyCacheRepository _repository = Substitute.For<ILobbyCacheRepository>();
    private readonly IMusicServiceClient _musicService = Substitute.For<IMusicServiceClient>();
    private readonly ILobbyCodeGenerator _codeGenerator = Substitute.For<ILobbyCodeGenerator>();
    private readonly LobbyManager _sut;

    public LobbyManagerTests()
    {
        _repository.GetLobbyIdForUserAsync(Arg.Any<int>()).Returns((Guid?)null);
        _repository.TryCreateAsync(Arg.Any<Lobby>()).Returns(CreateLobbyResult.Created);
        _musicService.GetPlaylistAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PlaylistDto(1, "90'er hits", 50));
        _codeGenerator.Generate().Returns("ABC234");

        _sut = new LobbyManager(_repository, _musicService, _codeGenerator, TimeProvider.System);
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
        _repository.GetLobbyIdForUserAsync(UserId).Returns(Guid.NewGuid());

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

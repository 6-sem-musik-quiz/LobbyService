using System.Security.Claims;
using LobbyService.Domain.Entities;
using LobbyService.Repository.Interfaces;
using LobbyService.Service.Hubs;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace LobbyService.Service.Tests;

public class LobbyHubTests
{
    private const string ConnectionId = "conn-1";

    private readonly ILobbyCacheRepository _repository = Substitute.For<ILobbyCacheRepository>();
    private readonly IGroupManager _groups = Substitute.For<IGroupManager>();
    private readonly HubCallerContext _context = Substitute.For<HubCallerContext>();
    private readonly LobbyHub _sut;

    public LobbyHubTests()
    {
        _context.ConnectionId.Returns(ConnectionId);
        _sut = new LobbyHub(_repository) { Context = _context, Groups = _groups };
    }

    private void SignInAs(string? sub) =>
        _context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(
            sub is null ? [] : [new Claim("sub", sub)], "Test")));

    private Lobby LobbyWith(params int[] userIds)
    {
        var lobby = new Lobby
        {
            Id = Guid.NewGuid(),
            HostUserId = userIds[0],
            Players = userIds.Select(id => new LobbyPlayer { UserId = id, UserName = $"user{id}" }).ToList()
        };
        _repository.GetAsync(lobby.Id).Returns(lobby);
        return lobby;
    }

    [Fact]
    public async Task JoinLobbyGroup_Member_AddsConnectionToLobbyGroup()
    {
        SignInAs("42");
        var lobby = LobbyWith(7, 42);

        await _sut.JoinLobbyGroup(lobby.Id);

        await _groups.Received(1).AddToGroupAsync(ConnectionId, $"lobby:{lobby.Id}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinLobbyGroup_NotMember_ThrowsHubException()
    {
        SignInAs("42");
        var lobby = LobbyWith(7, 8);

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinLobbyGroup(lobby.Id));

        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Fact]
    public async Task JoinLobbyGroup_UnknownLobby_ThrowsHubException()
    {
        SignInAs("42");
        var id = Guid.NewGuid();
        _repository.GetAsync(id).Returns((Lobby?)null);

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinLobbyGroup(id));

        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Fact]
    public async Task JoinLobbyGroup_NoUserId_ThrowsHubException()
    {
        SignInAs(null);
        var lobby = LobbyWith(7);

        await Assert.ThrowsAsync<HubException>(() => _sut.JoinLobbyGroup(lobby.Id));

        await _repository.DidNotReceive().GetAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task LeaveLobbyGroup_RemovesConnectionFromLobbyGroup()
    {
        var id = Guid.NewGuid();

        await _sut.LeaveLobbyGroup(id);

        await _groups.Received(1).RemoveFromGroupAsync(ConnectionId, $"lobby:{id}", Arg.Any<CancellationToken>());
    }
}

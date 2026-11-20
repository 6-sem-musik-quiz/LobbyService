namespace LobbyService.Service.Interfaces;

public interface ILobbyCodeGenerator
{
    // 6 characters from A-Z2-9 without 0/O/1/I.
    string Generate();
}

using System.Security.Cryptography;
using LobbyService.Service.Interfaces;

namespace LobbyService.Service.Services;

public class LobbyCodeGenerator : ILobbyCodeGenerator
{
    // A-Z and 2-9 without the look-alikes 0, O, 1 and I.
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    public string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);
}

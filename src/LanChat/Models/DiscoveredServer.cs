namespace LanChat.Models;

/// <summary>Сервер, найденный в локальной сети по UDP-маяку.</summary>
public sealed class DiscoveredServer
{
    public required string Address { get; init; }
    public required int Port { get; init; }
    public required string Room { get; init; }
    public int UserCount { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string Key => $"{Address}:{Port}";
    public string Title => Room;
    public string Subtitle => $"{Address}:{Port}  ·  участников: {UserCount}";
}

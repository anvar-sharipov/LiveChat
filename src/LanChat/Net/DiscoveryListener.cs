using System.Net;
using System.Net.Sockets;
using System.Text;
using LanChat.Models;

namespace LanChat.Net;

/// <summary>
/// Сторона клиента: слушает UDP-маяки серверов и ведёт актуальный список найденных
/// чатов. Сервер, от которого давно не было маяка, из списка пропадает.
/// </summary>
public sealed class DiscoveryListener : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, DiscoveredServer> _servers = [];
    private readonly Lock _gate = new();
    private Task? _loop;
    private Task? _purge;
    private bool _disposed;

    /// <summary>Список найденных серверов изменился (появился, пропал или обновился).</summary>
    public event Action<IReadOnlyList<DiscoveredServer>>? ServersChanged;

    public DiscoveryListener()
    {
        _udp = new UdpClient();
        // ReuseAddress нужен, чтобы сервер и клиент могли работать на одной машине.
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
    }

    public void Start()
    {
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _purge = Task.Run(() => PurgeLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            var beacon = Beacon.FromJson(Encoding.UTF8.GetString(result.Buffer));
            if (beacon is null) continue;

            var address = result.RemoteEndPoint.Address.ToString();
            var key = $"{address}:{beacon.Port}";
            var changed = false;

            lock (_gate)
            {
                if (_servers.TryGetValue(key, out var existing))
                {
                    changed = existing.UserCount != beacon.Users || existing.Room != beacon.Room;
                    existing.UserCount = beacon.Users;
                    existing.LastSeen = DateTime.UtcNow;
                }
                else
                {
                    _servers[key] = new DiscoveredServer
                    {
                        Address = address,
                        Port = beacon.Port,
                        Room = beacon.Room,
                        UserCount = beacon.Users,
                    };
                    changed = true;
                }
            }

            if (changed) RaiseChanged();
        }
    }

    private async Task PurgeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }

            var removed = false;
            lock (_gate)
            {
                var dead = _servers
                    .Where(kv => DateTime.UtcNow - kv.Value.LastSeen > Protocol.ServerTimeout)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in dead)
                {
                    _servers.Remove(key);
                    removed = true;
                }
            }

            if (removed) RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        List<DiscoveredServer> snapshot;
        lock (_gate) snapshot = _servers.Values.OrderBy(s => s.Room).ToList();
        ServersChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _udp.Dispose();
        try { Task.WhenAll(_loop ?? Task.CompletedTask, _purge ?? Task.CompletedTask).Wait(TimeSpan.FromSeconds(1)); }
        catch { /* ignore */ }
        _cts.Dispose();
    }
}

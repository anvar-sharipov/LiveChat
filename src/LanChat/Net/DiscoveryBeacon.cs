using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LanChat.Net;

/// <summary>
/// Сторона сервера: раз в секунду рассылает по локальной сети UDP-пакет «я тут,
/// подключайтесь», чтобы клиентам не нужно было вводить IP руками.
/// </summary>
public sealed class DiscoveryBeacon : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<Beacon> _snapshot;
    private Task? _loop;
    private bool _disposed;

    public DiscoveryBeacon(Func<Beacon> snapshot)
    {
        _snapshot = snapshot;
        _udp = new UdpClient { EnableBroadcast = true };
    }

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(_snapshot().ToJson());
                foreach (var target in BroadcastTargets())
                {
                    try { await _udp.SendAsync(bytes, new IPEndPoint(target, Protocol.DiscoveryPort), ct); }
                    catch (SocketException) { /* интерфейс отвалился — не беда, шлём в остальные */ }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            try { await Task.Delay(Protocol.BeaconInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Адреса для рассылки: общий 255.255.255.255 плюс «направленный» broadcast
    /// каждого активного IPv4-интерфейса — так пакет уходит во все сети машины,
    /// а не только в ту, что выбрала таблица маршрутизации.
    /// </summary>
    public static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask is null || Equals(ua.IPv4Mask, IPAddress.Any)) continue;

                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bcast);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        _udp.Dispose();
        _cts.Dispose();
    }
}

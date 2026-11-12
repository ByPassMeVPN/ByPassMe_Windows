using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ByPassMe.Helpers;

/// <summary>
/// Пинг серверов: TCP connect (как v2rayN GetTcpingTime).
/// Если TCP не ответил — fallback на ICMP для любого сервера из списка.
/// </summary>
public static class VpnPingService
{
    /// <summary>
    /// Последние замеры (id сервера → мс). Живут, пока открыто приложение, поэтому
    /// при переходе между вкладками цифры не пропадают — как на Android и iPhone.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Results = new();

    public static int? Cached(string serverId) =>
        Results.TryGetValue(serverId, out var ms) ? ms : null;

    public static void Remember(string serverId, int ms)
    {
        if (!string.IsNullOrEmpty(serverId))
            Results[serverId] = ms;
    }

    public static bool HasResults => !Results.IsEmpty;

    private const int TcpTimeoutMs = 4000;
    private const int IcmpTimeoutMs = 3000;

    public static Task<int> PingAsync(string host, int port, string? network = null, CancellationToken ct = default) =>
        PingCoreAsync(host, port, ct);

    /// <summary>Совместимость со старым вызовом.</summary>
    public static Task<int> TcpPingAsync(string host, int port, CancellationToken ct = default) =>
        PingCoreAsync(host, port, ct);

    private static async Task<int> PingCoreAsync(string host, int port, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or >= 65536)
            return -1;

        var ips = await ResolveIpv4Async(host, ct).ConfigureAwait(false);
        if (ips.Count == 0)
            return -1;

        // 1) TCP connect — основной замер
        foreach (var ip in ips)
        {
            ct.ThrowIfCancellationRequested();
            var tcp = await TcpConnectMsAsync(ip, port, ct).ConfigureAwait(false);
            if (tcp >= 0)
                return tcp;
        }

        // Повтор TCP
        foreach (var ip in ips)
        {
            ct.ThrowIfCancellationRequested();
            var tcp = await TcpConnectMsAsync(ip, port, ct).ConfigureAwait(false);
            if (tcp >= 0)
                return tcp;
        }

        // 2) ICMP — для любого сервера, если TCP не ответил
        foreach (var ip in ips)
        {
            ct.ThrowIfCancellationRequested();
            var icmp = await IcmpPingMsAsync(ip, ct).ConfigureAwait(false);
            if (icmp >= 0)
                return icmp;
        }

        return -1;
    }

    private static async Task<List<IPAddress>> ResolveIpv4Async(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            if (parsed.AddressFamily == AddressFamily.InterNetwork)
                return [parsed];
            try
            {
                if (parsed.IsIPv4MappedToIPv6)
                    return [parsed.MapToIPv4()];
            }
            catch { /* ignore */ }
            return [];
        }

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Distinct().ToList();
            if (v4.Count > 0)
                return v4;
        }
        catch
        {
            // ignore
        }

        return [];
    }

    private static async Task<int> TcpConnectMsAsync(IPAddress ip, int port, CancellationToken ct)
    {
        var endPoint = new IPEndPoint(ip, port);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        var timer = Stopwatch.StartNew();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TcpTimeoutMs);
            await socket.ConnectAsync(endPoint, linked.Token).ConfigureAwait(false);
            return Math.Max(1, (int)timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return -1;
        }
        catch
        {
            return -1;
        }
        finally
        {
            timer.Stop();
        }
    }

    private static async Task<int> IcmpPingMsAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            // .NET 8: SendPingAsync(IPAddress, int timeout) — без CancellationToken
            var replyTask = ping.SendPingAsync(ip, IcmpTimeoutMs);
            var completed = await Task.WhenAny(replyTask, Task.Delay(IcmpTimeoutMs + 500, ct)).ConfigureAwait(false);
            if (completed != replyTask)
                return -1;
            var reply = await replyTask.ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
                return Math.Max(1, (int)reply.RoundtripTime);
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch
        {
            // ICMP может быть запрещён политикой ОС
        }

        return -1;
    }
}

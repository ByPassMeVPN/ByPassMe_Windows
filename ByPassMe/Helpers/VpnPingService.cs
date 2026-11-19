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

    private const int ProxyTimeoutMs = 6000;

    /// <summary>
    /// Пинг сервера из списка. Если у сервера есть прокси — время только последнего
    /// шага: от команды CONNECT до ответа прокси «соединение с сервером установлено»
    /// (путь «ПК → прокси → сервер», как пинг в Happ), лучший из прокси. Подключение
    /// к прокси, приветствие и логин в цифру не входят — иначе это 4 круга до прокси.
    /// Прямой вход у таких серверов закрыт, прямой TCP-пинг показал бы «нет ответа».
    /// </summary>
    public static async Task<int> PingAsync(ByPassMe.Models.VpnServer server, CancellationToken ct = default)
    {
        var proxies = server.ActiveProxies;
        if (proxies.Count == 0)
            return await PingCoreAsync(server.Address, server.Port, ct).ConfigureAwait(false);

        var results = await Task.WhenAll(proxies.Select(p => ViaProxyMsAsync(p, server.Address, server.Port, ct)))
            .ConfigureAwait(false);
        var ok = results.Where(ms => ms >= 0).ToList();
        return ok.Count > 0 ? ok.Min() : -1;
    }

    private static async Task<int> ViaProxyMsAsync(ByPassMe.Models.VpnProxy p, string host, int port, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(ProxyTimeoutMs);
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(p.Host, p.Port, linked.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var ms = p.Type == "http"
                ? await HttpConnectAsync(stream, p, host, port, linked.Token).ConfigureAwait(false)
                : await Socks5ConnectAsync(stream, p, host, port, linked.Token).ConfigureAwait(false);
            return ms >= 0 ? Math.Max(1, ms) : -1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return buf;
    }

    /// <summary>
    /// SOCKS5 (RFC 1928/1929): приветствие, логин/пароль при наличии, CONNECT по имени/IP.
    /// Возвращает время шага CONNECT в мс (-1 — не получилось).
    /// </summary>
    private static async Task<int> Socks5ConnectAsync(NetworkStream s, ByPassMe.Models.VpnProxy p, string host, int port, CancellationToken ct)
    {
        await s.WriteAsync(p.HasAuth ? new byte[] { 5, 1, 2 } : new byte[] { 5, 1, 0 }, ct).ConfigureAwait(false);
        var greet = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
        if (greet[0] != 5) return -1;
        if (greet[1] == 2)
        {
            var u = System.Text.Encoding.UTF8.GetBytes(p.User);
            var pw = System.Text.Encoding.UTF8.GetBytes(p.Pass);
            var auth = new List<byte> { 1, (byte)u.Length };
            auth.AddRange(u);
            auth.Add((byte)pw.Length);
            auth.AddRange(pw);
            await s.WriteAsync(auth.ToArray(), ct).ConfigureAwait(false);
            var res = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
            if (res[1] != 0) return -1;
        }
        else if (greet[1] != 0)
        {
            return -1;
        }

        var req = new List<byte> { 5, 1, 0 };
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            req.Add(1);
            req.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var h = System.Text.Encoding.ASCII.GetBytes(host);
            req.Add(3);
            req.Add((byte)h.Length);
            req.AddRange(h);
        }
        req.Add((byte)(port >> 8));
        req.Add((byte)port);
        var timer = Stopwatch.StartNew();
        await s.WriteAsync(req.ToArray(), ct).ConfigureAwait(false);
        var head = await ReadExactAsync(s, 4, ct).ConfigureAwait(false);
        var took = (int)timer.ElapsedMilliseconds;
        return head[0] == 5 && head[1] == 0 ? took : -1;
    }

    /// <summary>HTTP CONNECT с Proxy-Authorization: Basic; успех — ответ 2xx. Время запроса в мс или -1.</summary>
    private static async Task<int> HttpConnectAsync(NetworkStream s, ByPassMe.Models.VpnProxy p, string host, int port, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder()
            .Append($"CONNECT {host}:{port} HTTP/1.1\r\n")
            .Append($"Host: {host}:{port}\r\n");
        if (p.HasAuth)
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{p.User}:{p.Pass}"));
            sb.Append($"Proxy-Authorization: Basic {token}\r\n");
        }
        sb.Append("\r\n");
        var timer = Stopwatch.StartNew();
        await s.WriteAsync(System.Text.Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);

        // Первая строка ответа: "HTTP/1.1 200 Connection established".
        var line = new System.Text.StringBuilder();
        var one = new byte[1];
        while (line.Length < 256)
        {
            var r = await s.ReadAsync(one, ct).ConfigureAwait(false);
            if (r <= 0 || one[0] == (byte)'\n') break;
            line.Append((char)one[0]);
        }
        var took = (int)timer.ElapsedMilliseconds;
        var parts = line.ToString().Split(' ');
        return parts.Length > 1 && int.TryParse(parts[1], out var code) && code is >= 200 and < 300 ? took : -1;
    }

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

using System.Net;
using System.Text;

namespace ByPassMe.Helpers;

/// <summary>
/// На Android приложение обхода исключено из VPN (excludeApplications).
/// На Windows аналог — split AllowedIPs: VK/VPS/локальные сети мимо туннеля,
/// чтобы bypassclient мог ходить на login.vk.ru и TURN при поднятом WG.
/// </summary>
public static class WireGuardConfigHelper
{
    private static readonly string[] ExcludeCidrs =
    [
        "95.163.0.0/16",
        "87.240.0.0/16",
        "93.186.224.0/20",
        "185.32.248.0/22",
        "185.29.130.0/24",
        "217.20.144.0/20",
        // VK TURN relays — must bypass WG on Windows (Android excludes app from VPN)
        "90.156.0.0/16",
        "193.203.0.0/16",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "127.0.0.0/8",
    ];

    public static string ApplySplitTunnel(string config, string? peerHost, IEnumerable<string>? extraExcludeHosts = null)
    {
        var excludes = new List<(uint Ip, int Bits)>();

        if (!string.IsNullOrWhiteSpace(peerHost) && IPAddress.TryParse(peerHost, out var peerIp))
        {
            var v4 = peerIp.MapToIPv4();
            if (v4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                excludes.Add((ToUint(v4), 32));
        }

        if (extraExcludeHosts != null)
        {
            foreach (var host in extraExcludeHosts)
            {
                if (IPAddress.TryParse(host, out var ip))
                {
                    var v4 = ip.MapToIPv4();
                    if (v4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        excludes.Add((ToUint(v4), 32));
                }
            }
        }

        foreach (var cidr in ExcludeCidrs)
        {
            if (TryParseCidr(cidr, out var block))
                excludes.Add(block);
        }

        var allowed = CalcAllowedIPs(excludes);
        var lines = config.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("AllowedIPs", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"AllowedIPs = {allowed}");
            else if (line.TrimStart().StartsWith("DNS", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("DNS = 77.88.8.8, 77.88.8.1");
            else
                sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    public static string? ExtractHost(string peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return null;
        var p = peer.Trim();
        if (p.StartsWith('['))
        {
            var end = p.IndexOf(']');
            if (end > 1) return p[1..end];
        }
        var colon = p.LastIndexOf(':');
        if (colon > 0 && p.Count(c => c == ':') == 1)
            return p[..colon];
        return p;
    }

    private static uint ToUint(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static bool TryParseCidr(string cidr, out (uint Ip, int Bits) block)
    {
        block = default;
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip))
            return false;
        if (!int.TryParse(parts[1], out var bits))
            return false;
        var v4 = ip.MapToIPv4();
        if (v4.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        block = (ToUint(v4), bits);
        return true;
    }

    private static string CalcAllowedIPs(List<(uint Ip, int Bits)> excludes)
    {
        var result = new List<(uint Ip, int Bits)>();
        SplitRec((0u, 0), excludes, result);

        return string.Join(", ", result.Select(r =>
        {
            var ip = new IPAddress(new byte[]
            {
                (byte)(r.Ip >> 24), (byte)(r.Ip >> 16), (byte)(r.Ip >> 8), (byte)r.Ip
            });
            return $"{ip}/{r.Bits}";
        }));
    }

    private static void SplitRec((uint Ip, int Bits) block, List<(uint Ip, int Bits)> excludes, List<(uint Ip, int Bits)> result)
    {
        foreach (var ex in excludes)
        {
            if (Contains(ex, block)) return;
        }

        var hasOverlap = excludes.Any(ex => Overlaps(block, ex));
        if (!hasOverlap)
        {
            result.Add(block);
            return;
        }
        if (block.Bits >= 32) return;

        var next = block.Bits + 1;
        var bit = 1u << (32 - next);
        SplitRec((block.Ip, next), excludes, result);
        SplitRec((block.Ip | bit, next), excludes, result);
    }

    private static bool Contains((uint Ip, int Bits) container, (uint Ip, int Bits) target)
    {
        if (container.Bits > target.Bits) return false;
        var mask = container.Bits == 0 ? 0u : 0xFFFFFFFFu << (32 - container.Bits);
        return (container.Ip & mask) == (target.Ip & mask);
    }

    private static bool Overlaps((uint Ip, int Bits) a, (uint Ip, int Bits) b)
    {
        var minBits = Math.Min(a.Bits, b.Bits);
        var mask = minBits == 0 ? 0u : 0xFFFFFFFFu << (32 - minBits);
        return (a.Ip & mask) == (b.Ip & mask);
    }
}

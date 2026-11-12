using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ByPassMe.Helpers;

/// <summary>
/// Физический WAN и хост-маршруты, чтобы сам xray не уходил в TUN.
/// DNS публичных резолверов в туннель не пиним — они должны идти через VPN, как на Android.
/// </summary>
public static class NetworkRouteHelper
{
    public const string TunName = "xray0";
    public const string TunNameRetry = "xray1";

    public sealed record WanLink(string Name, string Gateway, string Ipv4, int IfIndex);

    private static readonly string[] KnownTunNames =
    [
        TunName, TunNameRetry, "bpm0", "xray_tun",
        SingboxTunConfigBuilder.VpnInterfaceName,
        SingboxTunConfigBuilder.BypassInterfaceName, "wintunsingbox_tun",
        Services.BypassManager.RawTunAdapterName,
    ];

    public static WanLink? DetectWan()
    {
        WanLink? fallback = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            if (IsTunnelOrVirtualNic(nic))
                continue;

            var props = nic.GetIPProperties();
            var gw = props.GatewayAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (gw == null)
                continue;

            var ipv4 = props.UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a =>
                    a.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(a)
                    && !IsLinkLocal(a));
            if (ipv4 == null)
                continue;

            int index;
            try { index = props.GetIPv4Properties().Index; }
            catch { index = 0; }

            var link = new WanLink(nic.Name, gw.ToString(), ipv4.ToString(), index);
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                return link;
            fallback ??= link;
        }

        return fallback;
    }

    public static bool IsTunUp(string? name = null)
    {
        try
        {
            var names = string.IsNullOrWhiteSpace(name)
                ? KnownTunNames
                : new[] { name };
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                names.Any(t => n.Name.Equals(t, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    public static List<IPAddress> ResolveIpv4(string host)
    {
        var result = new List<IPAddress>();
        if (IPAddress.TryParse(host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            result.Add(parsed);
            return result;
        }

        try
        {
            result.AddRange(Dns.GetHostAddresses(host)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork));
        }
        catch
        {
            /* DNS ещё без туннеля — если не вышло, хост-маршрут не ставим */
        }

        return result;
    }

    public static void AddHostRoutes(IEnumerable<IPAddress> ips, WanLink wan)
    {
        foreach (var ip in ips.Distinct())
        {
            var iface = wan.IfIndex > 0 ? $" if {wan.IfIndex}" : "";
            RunRoute($"add {ip} mask 255.255.255.255 {wan.Gateway} metric 1{iface}");
        }
    }

    public static void RemoveHostRoutes(IEnumerable<IPAddress> ips, WanLink wan)
    {
        foreach (var ip in ips.Distinct())
            RunRoute($"delete {ip} mask 255.255.255.255 {wan.Gateway}");
    }

    public static void RemoveTunAdapter()
    {
        foreach (var name in KnownTunNames)
        {
            RunHidden("netsh", $"interface set interface name=\"{name}\" admin=disabled");
            RunHidden("netsh", $"interface delete interface name=\"{name}\"");
            RemoveWintunDevice(name);
        }
    }

    /// <summary>
    /// GUID адаптера = MD5(имя) — так xray-core создаёт wintun (proxy/tun/tun_windows.go).
    /// </summary>
    public static void RemoveWintunDevice(string adapterName)
    {
        try
        {
            var guid = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(adapterName)));
            RunHidden("pnputil.exe", $"/remove-device \"SWD\\Wintun\\{{{guid}}}\"");
        }
        catch
        {
            /* ignore */
        }
    }

    private static bool IsTunnelOrVirtualNic(NetworkInterface nic)
    {
        var name = nic.Name;
        var desc = nic.Description;
        if (name.Equals(TunName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(TunNameRetry, StringComparison.OrdinalIgnoreCase)
            || name.Equals("bpm0", StringComparison.OrdinalIgnoreCase)
            || name.Equals(SingboxTunConfigBuilder.VpnInterfaceName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(SingboxTunConfigBuilder.BypassInterfaceName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("xray", StringComparison.OrdinalIgnoreCase)
            || name.Contains("singbox", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
            return true;

        return ContainsAny(name, VirtualTokens) || ContainsAny(desc, VirtualTokens);
    }

    private static readonly string[] VirtualTokens =
    [
        "Wintun", "WireGuard", "ByPassMe", "Hyper-V", "VMware", "VirtualBox",
        "TAP-Windows", "VPN", "Bluetooth", "Wi-Fi Direct", "WAN Miniport",
    ];

    private static bool ContainsAny(string text, string[] tokens) =>
        tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static bool IsLinkLocal(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    private static void RunRoute(string args) => RunHidden("route", args);

    private static void RunHidden(string file, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc?.WaitForExit(4000);
        }
        catch
        {
            /* ignore */
        }
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ByPassMe.Helpers;

/// <summary>
/// Подготовка сети перед запуском Обхода — как на Android (TunnelManager) и iOS
/// (BypassManager): адреса хостов VK и доменов «напрямую» приложение получает
/// заранее своим резолвером, а Go-клиент берёт готовые IP. Во время глушения
/// интернета резолвер внутри клиента часто молчит, и без этого туннель
/// поднимается минутами или не поднимается вовсе.
/// </summary>
public static class BypassNetworkPrep
{
    private static readonly string[] VkHosts =
    [
        "login.vk.ru", "api.vk.ru", "api.vk.me", "calls.okcdn.ru",
        "id.vk.com", "id.vk.ru", "vk.com", "vk.ru",
    ];

    /// <summary>Яндекс — запасной DNS, если DNS сети не отвечает.</summary>
    private static readonly string[] YandexDns = ["77.88.8.8", "77.88.8.1"];

    /// <summary>JSON {хост: [ip]} для -vk-host-ips.</summary>
    public static async Task<string> VkHostIpsJsonAsync(TimeSpan timeout)
    {
        var map = await ResolveManyAsync(VkHosts, timeout).ConfigureAwait(false);
        return map.Count == 0 ? "" : JsonSerializer.Serialize(map);
    }

    /// <summary>IP доменов «напрямую» — они пойдут мимо туннеля (-exclude-ips).</summary>
    public static async Task<string> DirectDomainIpsAsync(TimeSpan timeout)
    {
        var map = await ResolveManyAsync(BypassDirectDomains.All, timeout).ConfigureAwait(false);
        var ips = map.Values.SelectMany(v => v).Distinct().ToList();
        return string.Join(",", ips);
    }

    /// <summary>DNS текущей сети + Яндекс — для -dns-fallback.</summary>
    public static string FallbackDnsCsv()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var dns in ni.GetIPProperties().DnsAddresses)
                {
                    if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(dns)) continue;
                    var s = dns.ToString();
                    if (!list.Contains(s)) list.Add(s);
                }
            }
        }
        catch { /* сеть может меняться прямо сейчас — берём хотя бы Яндекс */ }

        foreach (var y in YandexDns)
            if (!list.Contains(y)) list.Add(y);

        return string.Join(",", list);
    }

    private static async Task<Dictionary<string, List<string>>> ResolveManyAsync(
        IReadOnlyList<string> hosts, TimeSpan timeout)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (hosts.Count == 0) return result;

        using var cts = new CancellationTokenSource(timeout);
        var tasks = hosts.Distinct(StringComparer.OrdinalIgnoreCase).Select(async host =>
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token)
                    .ConfigureAwait(false);
                var ips = addrs.Select(a => a.ToString()).Distinct().ToList();
                return (host, ips);
            }
            catch
            {
                return (host, new List<string>());
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch { /* по таймауту берём то, что успело разрешиться */ }

        foreach (var t in tasks)
        {
            if (!t.IsCompletedSuccessfully) continue;
            var (host, ips) = t.Result;
            if (ips.Count > 0) result[host] = ips;
        }
        return result;
    }
}

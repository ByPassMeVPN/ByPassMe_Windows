using System.Net;
using ByPassMe.Models;

namespace ByPassMe.Helpers;

/// <summary>Домены, которые резолвятся напрямую (v2rayN ProtectDomainList).</summary>
public static class VpnProtectDomains
{
    public static List<string> Collect(VpnServer server)
    {
        var list = new List<string>();
        AddDomain(list, server.Address);
        AddDomain(list, server.Sni);
        return list;
    }

    public static List<string> CollectBypassPeer(string peer)
    {
        var host = peer.Contains(':') ? peer[..peer.IndexOf(':')] : peer;
        var list = new List<string>();
        AddDomain(list, host);
        return list;
    }

    private static void AddDomain(List<string> list, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (IPAddress.TryParse(value, out _)) return;
        if (list.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        list.Add(value);
    }
}

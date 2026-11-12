using ByPassMe.Models;

namespace ByPassMe.Helpers;

/// <summary>Подпись протокола в списке VPN (как v2rayN, без JSON).</summary>
public static class VpnProtocolHelper
{
    public static string Format(VpnServer server) =>
        !string.IsNullOrWhiteSpace(server.ProtocolLabel)
            ? server.ProtocolLabel
            : FormatByNetwork(server);

    private static string FormatByNetwork(VpnServer server) =>
        server.Network.ToLowerInvariant() switch
        {
            "xhttp" or "splithttp" => "VLESS / XHTTP / TLS",
            "tcp" => "VLESS / TCP / REALITY",
            "hysteria" => "HYSTERIA / HYSTERIA / TLS",
            "grpc" => "VLESS / GRPC / REALITY",
            _ => server.Network.ToUpperInvariant(),
        };
}

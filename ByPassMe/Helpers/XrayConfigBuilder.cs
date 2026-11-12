using System.IO;
using System.Text.Json.Nodes;
using ByPassMe.Models;

namespace ByPassMe.Helpers;

/// <summary>
/// xray JSON — 1:1 с Android XrayConfigBuilder.kt (только SOCKS/HTTP).
/// TUN на Windows делает sing-box, как v2rayN Legacy Protect.
/// </summary>
public static class XrayConfigBuilder
{
    public const int SocksPort = 10808;
    private const int HttpPort = 10809;
    public const int SpeedtestSocksPort = 10810;
    private const int SpeedtestHttpPort = 10811;
    private const string GrpcServiceName = "vless-grpc-service";
    private const string RealityPublicKey = "jQC3Kmmbu-CfUKlQaeuSn4I8dbqAZhpgihZhWu1SvBk";
    private const string RealityShortId = "6b69118a7847c4c3";
    private const string RealitySni = "www.samsung.com";
    private const string RealityGrpcSni = "www.google.com";
    private const string RealityFlow = "xtls-rprx-vision";
    private const string HysteriaAuth = "f2df79dd-f7d0-4c8b-8c61-7b6fb9852758";
    private const string HysteriaAlpn = "h3";
    private const string XhttpPath = "/assets/build/_app/immutable/chunks/stream-one/";
    private const string XhttpMode = "auto";

    public static string ErrorLogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ByPassMe", "xray-error.log");

    public static string SpeedtestErrorLogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ByPassMe", "xray-ping-error.log");

    private const string SpeedtestProxyTag = "proxy";

    public static string Build(VpnServer server, string uuid, NetworkRouteHelper.WanLink? wan = null, string? connectIp = null) =>
        JsonNodeText.Write(BuildRoot(server, uuid, wan, connectIp, speedtest: false));

    /// <summary>Конфиг для Real Ping — отдельный порт SOCKS, весь трафик в proxy (как v2rayN speedtest).</summary>
    public static string BuildSpeedtest(VpnServer server, string uuid, NetworkRouteHelper.WanLink? wan = null, string? connectIp = null) =>
        JsonNodeText.Write(BuildRoot(server, uuid, wan, connectIp, speedtest: true));

    private static JsonObject BuildRoot(VpnServer server, string uuid, NetworkRouteHelper.WanLink? wan, string? connectIp, bool speedtest)
    {
        var proxyTag = speedtest ? SpeedtestProxyTag : server.OutboundTag;
        var socksPort = speedtest ? SpeedtestSocksPort : SocksPort;
        var httpPort = speedtest ? SpeedtestHttpPort : HttpPort;
        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = speedtest ? "warning" : "warning",
                ["access"] = "none",
                ["error"] = (speedtest ? SpeedtestErrorLogPath : ErrorLogPath).Replace('\\', '/'),
            },
            ["inbounds"] = speedtest ? InboundsSpeedtest(socksPort) : Inbounds(socksPort, httpPort, false),
            ["routing"] = speedtest ? RoutingSpeedtest(proxyTag) : Routing(proxyTag),
            ["outbounds"] = Outbounds(server, uuid, wan, connectIp, speedtest),
        };
        if (!speedtest)
            root["dns"] = Dns();
        return root;
    }

    private static JsonObject Dns() => new()
    {
        ["servers"] = new JsonArray("1.1.1.1", "9.9.9.9", "94.140.14.14", "1.0.0.1"),
        ["queryStrategy"] = "UseIPv4",
    };

    /// <summary>v2rayN speedtest: один inbound SOCKS, domainStrategy AsIs, final → proxy.</summary>
    private static JsonArray InboundsSpeedtest(int socksPort) => new(new JsonObject
    {
        ["tag"] = $"socks{socksPort}",
        ["port"] = socksPort,
        ["listen"] = "127.0.0.1",
        ["protocol"] = "mixed",
        ["settings"] = new JsonObject { ["udp"] = true, ["auth"] = "noauth" },
    });

    private static JsonArray Inbounds(int socksPort, int httpPort, bool speedtest)
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "socks",
                ["port"] = socksPort,
                ["listen"] = "127.0.0.1",
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["udp"] = true, ["auth"] = "noauth" },
                ["sniffing"] = Sniffing(routeOnly: !speedtest),
            },
            new JsonObject
            {
                ["tag"] = "http",
                ["port"] = httpPort,
                ["listen"] = "127.0.0.1",
                ["protocol"] = "http",
                ["settings"] = new JsonObject { ["allowTransparent"] = false },
                ["sniffing"] = Sniffing(routeOnly: false),
            },
        };
    }

    private static JsonObject Sniffing(bool routeOnly) => new()
    {
        ["enabled"] = true,
        ["routeOnly"] = routeOnly,
        ["destOverride"] = new JsonArray("http", "tls", "quic"),
    };

    /// <summary>
    /// Маршрутизация как в подписке Happ и на Android: РФ — напрямую, остальное
    /// в VPN, а YouTube/TikTok/Instagram/проверка IP — принудительно в VPN.
    /// </summary>
    private static JsonObject Routing(string proxyTag)
    {
        var rules = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "field",
                ["domain"] = ToArray(XrayRoutingLists.AppleBlockDomains),
                ["outboundTag"] = "block",
            },
            new JsonObject
            {
                ["type"] = "field",
                ["protocol"] = new JsonArray("bittorrent"),
                ["outboundTag"] = "block",
            },
            new JsonObject
            {
                ["type"] = "field",
                ["domain"] = ToArray(XrayRoutingLists.ForceVpnDomains),
                ["outboundTag"] = proxyTag,
            },
            new JsonObject
            {
                ["type"] = "field",
                ["domain"] = ToArray(XrayRoutingLists.RuDirectDomains),
                ["outboundTag"] = "direct",
            },
            new JsonObject
            {
                ["type"] = "field",
                ["ip"] = ToArray(XrayRoutingLists.PrivateCidrs),
                ["outboundTag"] = "direct",
            },
            new JsonObject
            {
                ["type"] = "field",
                ["network"] = "tcp,udp",
                ["outboundTag"] = proxyTag,
            },
        };

        return new JsonObject
        {
            ["domainMatcher"] = "hybrid",
            // AsIs: решение по имени сразу, без DNS-запроса перед соединением.
            ["domainStrategy"] = "AsIs",
            ["rules"] = rules,
        };
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var i in items) a.Add(i);
        return a;
    }

    private static JsonObject RoutingSpeedtest(string proxyTag) => new()
    {
        ["domainStrategy"] = "AsIs",
        ["rules"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "field",
                ["network"] = "tcp,udp",
                ["outboundTag"] = proxyTag,
            },
        },
    };

    private static string Dest(VpnServer server, string? connectIp) =>
        string.IsNullOrWhiteSpace(connectIp) ? server.Address : connectIp;

    private static JsonArray Outbounds(VpnServer server, string uuid, NetworkRouteHelper.WanLink? wan, string? connectIp, bool speedtest = false)
    {
        var dest = Dest(server, connectIp);
        var proxy = server.Network.ToLowerInvariant() switch
        {
            "hysteria" => HysteriaOutbound(server, uuid, dest),
            "tcp" => TcpRealityOutbound(server, uuid, dest),
            "xhttp" or "splithttp" => XhttpOutbound(server, uuid, dest),
            _ => GrpcRealityOutbound(server, uuid, dest),
        };
        if (speedtest)
            proxy["tag"] = SpeedtestProxyTag;
        BindToWan(proxy, wan);

        return new JsonArray
        {
            proxy,
            new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
        };
    }

    // sendThrough + sockopt.interface: если имя NIC не совпало с тем, что видит xray — всё равно не будет петли.
    private static void BindToWan(JsonObject outbound, NetworkRouteHelper.WanLink? wan)
    {
        if (wan == null) return;
        outbound["sendThrough"] = wan.Ipv4;

        if (string.IsNullOrWhiteSpace(wan.Name))
            return;

        if (outbound["streamSettings"] is not JsonObject stream)
        {
            stream = new JsonObject();
            outbound["streamSettings"] = stream;
        }

        if (stream["sockopt"] is not JsonObject sockopt)
        {
            sockopt = new JsonObject();
            stream["sockopt"] = sockopt;
        }

        sockopt["interface"] = wan.Name;
    }

    private static JsonObject HysteriaOutbound(VpnServer server, string uuid, string dest)
    {
        var auth = string.IsNullOrWhiteSpace(uuid) ? HysteriaAuth : uuid;
        var sni = string.IsNullOrWhiteSpace(server.Sni) ? server.Address : server.Sni;
        var fp = string.IsNullOrWhiteSpace(server.Fingerprint) ? "chrome" : server.Fingerprint;
        return new JsonObject
        {
            ["tag"] = server.OutboundTag,
            ["protocol"] = "hysteria",
            ["settings"] = new JsonObject
            {
                ["address"] = dest,
                ["port"] = server.Port,
                ["version"] = 2,
            },
            ["streamSettings"] = new JsonObject
            {
                ["network"] = "hysteria",
                ["security"] = "tls",
                ["tlsSettings"] = HysteriaTls(server, fp, sni),
                ["hysteriaSettings"] = new JsonObject
                {
                    ["auth"] = auth,
                    ["version"] = 2,
                },
                ["finalmask"] = new JsonObject { ["quicParams"] = QuicParams(server) },
                ["sockopt"] = new JsonObject { ["udpDomainStrategy"] = "UseIP" },
            },
        };
    }

    private static JsonObject QuicParams(VpnServer server)
    {
        var quic = new JsonObject
        {
            ["disablePathMTUDiscovery"] = false,
            ["initConnReceiveWindow"] = 20971520,
            ["initStreamReceiveWindow"] = 8388608,
            // GAMING SERVER (как в подписке Happ): brutal не сбрасывает скорость
            // при потерях, короткий keepalive/idle — быстрее замечаем обрыв.
            ["keepAlivePeriod"] = server.IsGaming ? 5 : 10,
            ["maxConnReceiveWindow"] = 20971520,
            ["maxIdleTimeout"] = server.IsGaming ? 15 : 30,
            ["maxIncomingStreams"] = 1024,
            ["maxStreamReceiveWindow"] = 8388608,
        };
        if (server.IsGaming)
            quic["congestion"] = "brutal";
        return quic;
    }

    private static JsonObject HysteriaTls(VpnServer server, string fp, string sni)
    {
        var tls = new JsonObject
        {
            ["alpn"] = new JsonArray(HysteriaAlpn),
            ["fingerprint"] = fp,
            ["serverName"] = sni,
        };
        if (server.IsGaming)
        {
            tls["cipherSuites"] = "TLS_AES_256_GCM_SHA384:TLS_CHACHA20_POLY1305_SHA256:TLS_AES_128_GCM_SHA256";
            tls["enableSessionResumption"] = true;
        }
        return tls;
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static JsonObject GrpcRealityOutbound(VpnServer server, string uuid, string dest) => new()
    {
        ["tag"] = server.OutboundTag,
        ["protocol"] = "vless",
        ["settings"] = new JsonObject
        {
            ["vnext"] = new JsonArray(new JsonObject
            {
                ["address"] = dest,
                ["port"] = server.Port,
                ["users"] = new JsonArray(new JsonObject
                {
                    ["id"] = uuid,
                    ["encryption"] = "none",
                }),
            }),
        },
        ["streamSettings"] = new JsonObject
        {
            ["network"] = "grpc",
            ["grpcSettings"] = new JsonObject
            {
                ["serviceName"] = GrpcServiceName,
                ["multiMode"] = false,
                ["idle_timeout"] = 60,
                ["health_check_timeout"] = 20,
                ["permit_without_stream"] = false,
                ["initial_windows_size"] = 0,
            },
            ["security"] = "reality",
            ["realitySettings"] = new JsonObject
            {
                ["serverName"] = RealityGrpcSni,
                ["publicKey"] = Or(server.PublicKey, RealityPublicKey),
                ["shortId"] = Or(server.ShortId, RealityShortId),
                ["fingerprint"] = server.Fingerprint,
            },
            ["sockopt"] = new JsonObject { ["tcpKeepAliveInterval"] = 30 },
        },
    };

    private static JsonObject TcpRealityOutbound(VpnServer server, string uuid, string dest)
    {
        var sni = string.IsNullOrWhiteSpace(server.Sni) ? RealitySni : server.Sni;
        var fp = string.IsNullOrWhiteSpace(server.Fingerprint) ? "firefox" : server.Fingerprint;
        return new JsonObject
        {
            ["tag"] = server.OutboundTag,
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = dest,
                    ["port"] = server.Port,
                    ["users"] = new JsonArray(new JsonObject
                    {
                        ["id"] = uuid,
                        ["encryption"] = "none",
                        ["flow"] = RealityFlow,
                    }),
                }),
            },
            ["streamSettings"] = new JsonObject
            {
                ["network"] = "tcp",
                ["tcpSettings"] = new JsonObject(),
                ["security"] = "reality",
                ["realitySettings"] = new JsonObject
                {
                    ["serverName"] = sni,
                    ["publicKey"] = Or(server.PublicKey, RealityPublicKey),
                    ["shortId"] = Or(server.ShortId, RealityShortId),
                    ["fingerprint"] = fp,
                },
                ["sockopt"] = new JsonObject { ["tcpKeepAliveInterval"] = 30 },
            },
        };
    }

    private static JsonObject XhttpOutbound(VpnServer server, string uuid, string dest)
    {
        var sni = string.IsNullOrWhiteSpace(server.Sni) ? server.Address : server.Sni;
        var path = string.IsNullOrWhiteSpace(server.Path) ? XhttpPath : server.Path;
        var fp = string.IsNullOrWhiteSpace(server.Fingerprint) ? "qq" : server.Fingerprint;
        return new JsonObject
        {
            ["tag"] = server.OutboundTag,
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = dest,
                    ["port"] = server.Port,
                    ["users"] = new JsonArray(new JsonObject
                    {
                        ["id"] = uuid,
                        ["encryption"] = "none",
                        ["flow"] = "",
                    }),
                }),
            },
            ["streamSettings"] = new JsonObject
            {
                ["network"] = "xhttp",
                ["security"] = "tls",
                ["tlsSettings"] = new JsonObject
                {
                    ["alpn"] = new JsonArray("h2", "http/1.1"),
                    ["fingerprint"] = fp,
                    ["serverName"] = sni,
                },
                ["xhttpSettings"] = new JsonObject
                {
                    ["path"] = path,
                    ["host"] = sni,
                    ["mode"] = XhttpMode,
                    ["extra"] = new JsonObject
                    {
                        ["scMaxEachPostBytes"] = "393216-786432",
                        ["xPaddingBytes"] = "100-500",
                        ["xmux"] = new JsonObject
                        {
                            ["cMaxReuseTimes"] = "200-300",
                            ["hKeepAlivePeriod"] = 60,
                            ["hMaxRequestTimes"] = "200-300",
                            ["hMaxReusableSecs"] = "600-900",
                            ["maxConnections"] = 1,
                        },
                    },
                },
                ["sockopt"] = new JsonObject { ["tcpKeepAliveInterval"] = 30 },
            },
        };
    }
}

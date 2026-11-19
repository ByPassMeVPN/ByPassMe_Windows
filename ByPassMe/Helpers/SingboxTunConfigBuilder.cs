using System.IO;
using System.Text.Json.Nodes;

namespace ByPassMe.Helpers;

/// <summary>
/// sing-box TUN — схема v2rayN 7.24.8 Legacy Protect.
/// VPN: geosite РФ + .ru/.su/.рф → direct; остальное → SOCKS xray (final: proxy).
/// Без geoip-ru в маршрутах: иностранные домены (.io/.net/…) идут в VPN даже с CDN в РФ.
/// Обход Б/С: белый список → direct-wan; остальное → WireGuard (final: direct-wg).
/// </summary>
public static class SingboxTunConfigBuilder
{
    public const string VpnInterfaceName = "singbox_tun";
    public const string BypassInterfaceName = "bypass_tun";
    public const string VpnTunAddress = "172.18.0.1";
    public const string BypassTunAddress = "172.19.0.1";
    /// <summary>clash_api для статистики трафика (как v2rayN StatePort2).</summary>
    public const int VpnClashApiPort = 19090;
    public const int BypassClashApiPort = 19091;

  private const string DnsBootstrap = "local";
  private const string DnsDirect = "direct";
  private const string DnsRemote = "remote";
  private const string RuDnsServer = "77.88.8.8";
  private const string ForeignDnsServer = "1.1.1.1";

    public static string BuildVpn(
        int socksPort,
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> processPaths,
        IReadOnlyList<string>? protectDomains = null,
        string? ruleSetDir = null,
        string? cachePath = null)
    {
        var rules = BaseRules(VpnTunAddress, processNames, processPaths);
        var (ruleSets, dnsRules) = AddVpnRuDirectRules(rules, ruleSetDir, protectDomains);

        return WriteConfig(
            VpnInterfaceName,
            VpnTunAddress,
            proxySocksPort: socksPort,
            wanInterface: null,
            wireGuardInterface: null,
            rules,
            ruleSets,
            dnsRules,
            vpnDns: true,
            cachePath: cachePath ?? DefaultCachePath("singbox-vpn-cache.db"),
            protectDomains: protectDomains,
            clashApiPort: VpnClashApiPort);
    }

    public static string BuildBypass(
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> processPaths,
        string wanInterface,
        string wireGuardInterface,
        IReadOnlyList<string>? protectDomains = null,
        string? cachePath = null)
    {
        var rules = BaseRules(BypassTunAddress, processNames, processPaths);
        var dnsRules = new JsonArray();
        var domains = WhitelistDomainArray();
        if (domains.Count > 0)
        {
            rules.Add(new JsonObject
            {
                ["domain"] = domains,
                ["outbound"] = "direct-wan",
            });
            dnsRules.Add(new JsonObject
            {
                ["domain"] = domains.DeepClone(),
                ["server"] = DnsBootstrap,
            });
        }

        AddProtectDnsRules(dnsRules, protectDomains);

        return WriteConfig(
            BypassInterfaceName,
            BypassTunAddress,
            proxySocksPort: null,
            wanInterface: wanInterface,
            wireGuardInterface: wireGuardInterface,
            rules,
            ruleSets: null,
            dnsRules,
            vpnDns: false,
            cachePath: cachePath ?? DefaultCachePath("singbox-bypass-cache.db"),
            protectDomains: protectDomains,
            clashApiPort: BypassClashApiPort);
    }

    private static string DefaultCachePath(string fileName) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ByPassMe", fileName).Replace('\\', '/');

    /// <summary>Порядок как v2rayN tun_singbox_rules + protect + sniff.</summary>
    private static JsonArray BaseRules(
        string tunAddress,
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> processPaths)
    {
        JsonArray Names()
        {
            var a = new JsonArray();
            foreach (var n in processNames)
                a.Add(n);
            return a;
        }

        JsonArray Paths()
        {
            var a = new JsonArray();
            foreach (var p in processPaths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    a.Add(p.Replace('/', '\\'));
            }
            return a;
        }

        var rules = new JsonArray
        {
            new JsonObject
            {
                ["network"] = new JsonArray("udp"),
                ["port"] = new JsonArray(135, 137, 138, 139, 5353),
                ["action"] = "reject",
            },
            new JsonObject
            {
                ["network"] = new JsonArray("udp"),
                ["port"] = new JsonArray(443),
                ["action"] = "reject",
            },
            new JsonObject
            {
                ["ip_cidr"] = new JsonArray("224.0.0.0/3", "ff00::/8"),
                ["action"] = "reject",
            },
            new JsonObject
            {
                ["ip_cidr"] = new JsonArray($"{tunAddress}/32"),
                ["action"] = "reject",
                ["method"] = "drop",
            },
            new JsonObject
            {
                ["network"] = new JsonArray("icmp"),
                ["outbound"] = "direct",
            },
        };

        var paths = Paths();
        if (paths.Count > 0)
        {
            rules.Add(new JsonObject
            {
                ["port"] = new JsonArray(53),
                ["action"] = "hijack-dns",
                ["process_path"] = paths,
            });
            rules.Add(new JsonObject
            {
                ["outbound"] = "direct",
                ["process_path"] = paths.DeepClone(),
            });
        }

        if (processNames.Count > 0)
        {
            var names = Names();
            rules.Add(new JsonObject
            {
                ["port"] = new JsonArray(53),
                ["action"] = "hijack-dns",
                ["process_name"] = names,
            });
            rules.Add(new JsonObject
            {
                ["outbound"] = "direct",
                ["process_name"] = names.DeepClone(),
            });
        }

        rules.Add(new JsonObject { ["action"] = "sniff" });
        rules.Add(new JsonObject
        {
            ["type"] = "logical",
            ["mode"] = "or",
            ["action"] = "hijack-dns",
            ["rules"] = new JsonArray(
                new JsonObject { ["port"] = new JsonArray(53) },
                new JsonObject { ["protocol"] = new JsonArray("dns") }),
        });

        rules.Add(new JsonObject
        {
            ["ip_is_private"] = true,
            ["outbound"] = "direct",
        });

        return rules;
    }

    private static (JsonArray? RuleSets, JsonArray DnsRules) AddVpnRuDirectRules(
        JsonArray rules,
        string? ruleSetDir,
        IReadOnlyList<string>? protectDomains)
    {
        var dnsRules = new JsonArray();
        var geositeTags = new JsonArray();
        var ruleSets = new JsonArray();

        if (!string.IsNullOrWhiteSpace(ruleSetDir) && Directory.Exists(ruleSetDir))
        {
            foreach (var (file, tag) in RuSplitRouting.BundledRuleSets)
            {
                var path = Path.GetFullPath(Path.Combine(ruleSetDir, file));
                if (!File.Exists(path) || new FileInfo(path).Length < 32)
                    continue;

                ruleSets.Add(new JsonObject
                {
                    ["tag"] = tag,
                    ["type"] = "local",
                    ["format"] = "binary",
                    ["path"] = path.Replace('\\', '/'),
                });
                geositeTags.Add(tag);
            }
        }

        // Строго ДО правил «РФ напрямую»: YouTube, TikTok и сайты проверки IP
        // (2ip.ru и т.п.) должны идти в VPN, хотя часть из них в зоне .ru.
        var forceVpn = ForceVpnSuffixes();
        if (forceVpn.Count > 0)
        {
            rules.Add(new JsonObject
            {
                ["domain_suffix"] = forceVpn,
                ["outbound"] = "proxy",
            });
        }

        if (geositeTags.Count > 0)
        {
            rules.Add(new JsonObject
            {
                ["rule_set"] = geositeTags,
                ["outbound"] = "direct",
            });
        }

        rules.Add(new JsonObject
        {
            ["domain_suffix"] = RuSplitRouting.TldSuffixJson(),
            ["outbound"] = "direct",
        });

        rules.Add(new JsonObject
        {
            ["action"] = "resolve",
            ["ip_version"] = 4,
        });

        var protect = new JsonArray();
        foreach (var d in protectDomains ?? [])
        {
            if (string.IsNullOrWhiteSpace(d) || System.Net.IPAddress.TryParse(d, out _))
                continue;
            protect.Add(d);
        }

        AddProtectDnsRules(dnsRules, protectDomains);

        if (geositeTags.Count > 0)
        {
            dnsRules.Add(new JsonObject
            {
                ["rule_set"] = geositeTags.DeepClone(),
                ["server"] = DnsDirect,
            });
        }

        if (forceVpn.Count > 0)
        {
            dnsRules.Add(new JsonObject
            {
                ["domain_suffix"] = ForceVpnSuffixes(),
                ["server"] = DnsRemote,
            });
        }

        dnsRules.Add(new JsonObject
        {
            ["domain_suffix"] = RuSplitRouting.TldSuffixJson(),
            ["server"] = DnsDirect,
        });

        return (ruleSets.Count > 0 ? ruleSets : null, dnsRules);
    }

    /// <summary>Домены «всегда через VPN» из общего списка (xray + sing-box).</summary>
    private static JsonArray ForceVpnSuffixes()
    {
        var a = new JsonArray();
        foreach (var d in XrayRoutingLists.ForceVpnDomains)
        {
            if (!d.StartsWith("domain:", StringComparison.Ordinal))
                continue; // geosite:* умеет только xray — там эти же домены перечислены поимённо
            a.Add(d["domain:".Length..]);
        }
        return a;
    }

    private static void AddProtectDnsRules(JsonArray dnsRules, IReadOnlyList<string>? protectDomains)
    {
        var protect = new JsonArray();
        foreach (var d in protectDomains ?? [])
        {
            if (string.IsNullOrWhiteSpace(d) || System.Net.IPAddress.TryParse(d, out _))
                continue;
            protect.Add(d);
        }

        if (protect.Count == 0) return;

        dnsRules.Add(new JsonObject
        {
            ["domain"] = protect,
            ["server"] = DnsBootstrap,
        });
    }

    private static JsonArray WhitelistDomainArray()
    {
        var a = new JsonArray();
        foreach (var d in BypassDirectDomains.All)
            a.Add(d);
        return a;
    }

    private static string WriteConfig(
        string interfaceName,
        string tunAddress,
        int? proxySocksPort,
        string? wanInterface,
        string? wireGuardInterface,
        JsonArray rules,
        JsonArray? ruleSets,
        JsonArray dnsRules,
        bool vpnDns,
        string cachePath,
        IReadOnlyList<string>? protectDomains,
        int clashApiPort)
    {
        var outbounds = new JsonArray();
        if (proxySocksPort is int port)
        {
            outbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "proxy",
                ["server"] = "127.0.0.1",
                ["server_port"] = port,
                ["version"] = "5",
                ["domain_resolver"] = new JsonObject
                {
                    ["server"] = DnsRemote,
                    ["strategy"] = "ipv4_only",
                },
            });
        }

        outbounds.Add(new JsonObject
        {
            ["type"] = "direct",
            ["tag"] = "direct",
            ["domain_resolver"] = new JsonObject
            {
                ["server"] = vpnDns ? DnsDirect : DnsBootstrap,
                ["strategy"] = "ipv4_only",
            },
        });

        if (!string.IsNullOrWhiteSpace(wanInterface))
        {
            outbounds.Add(new JsonObject
            {
                ["type"] = "direct",
                ["tag"] = "direct-wan",
                ["bind_interface"] = wanInterface,
                ["domain_resolver"] = new JsonObject
                {
                    ["server"] = DnsBootstrap,
                    ["strategy"] = "ipv4_only",
                },
            });
        }

        if (!string.IsNullOrWhiteSpace(wireGuardInterface))
        {
            outbounds.Add(new JsonObject
            {
                ["type"] = "direct",
                ["tag"] = "direct-wg",
                ["bind_interface"] = wireGuardInterface,
                ["domain_resolver"] = new JsonObject
                {
                    ["server"] = DnsBootstrap,
                    ["strategy"] = "ipv4_only",
                },
            });
        }

        var routeFinal = proxySocksPort.HasValue
            ? "proxy"
            : !string.IsNullOrWhiteSpace(wireGuardInterface)
                ? "direct-wg"
                : "direct";

        var route = new JsonObject
        {
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = new JsonObject
            {
                ["server"] = vpnDns ? DnsDirect : DnsBootstrap,
                ["strategy"] = "ipv4_only",
            },
            ["final"] = routeFinal,
            ["rules"] = rules,
        };
        if (ruleSets is { Count: > 0 })
            route["rule_set"] = ruleSets;

        var dnsServers = new JsonArray(
            new JsonObject { ["tag"] = DnsBootstrap, ["type"] = "local" });

        if (vpnDns)
        {
            dnsServers.Add(new JsonObject
            {
                ["tag"] = DnsDirect,
                ["type"] = "udp",
                ["server"] = RuDnsServer,
                ["domain_resolver"] = DnsBootstrap,
            });
            dnsServers.Add(new JsonObject
            {
                ["tag"] = DnsRemote,
                ["type"] = "udp",
                ["server"] = ForeignDnsServer,
                ["detour"] = "proxy",
                ["domain_resolver"] = DnsBootstrap,
            });
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["dns"] = new JsonObject
            {
                ["servers"] = dnsServers,
                ["rules"] = dnsRules,
                ["final"] = vpnDns ? DnsRemote : DnsBootstrap,
                ["strategy"] = "ipv4_only",
                ["optimistic"] = true,
            },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun",
                ["interface_name"] = interfaceName,
                ["address"] = new JsonArray($"{tunAddress}/30"),
                ["mtu"] = 9000,
                ["auto_route"] = true,
                ["strict_route"] = true,
                ["stack"] = "gvisor",
                ["route_exclude_address"] = new JsonArray(
                    "192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12",
                    "127.0.0.0/8", "169.254.0.0/16"),
            }),
            ["outbounds"] = outbounds,
            ["route"] = route,
            ["experimental"] = new JsonObject
            {
                ["cache_file"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["path"] = cachePath,
                    ["store_dns"] = true,
                },
                // Как v2rayN GenExperimental: clash_api → ws://…/traffic (↑/↓ B/s)
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{clashApiPort}",
                },
            },
        };

        return JsonNodeText.Write(root);
    }
}

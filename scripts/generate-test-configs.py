#!/usr/bin/env python3
"""Генерирует sing-box JSON зеркально C# SingboxTunConfigBuilder."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / ".cache"
ASSETS = ROOT / "ByPassMe" / "Assets"
RU_TLDS = ["ru", "su", "xn--p1ai"]
BUNDLED = [
    ("geosite-category-ru.srs", "geosite-category-ru"),
    ("geosite-category-gov-ru.srs", "geosite-category-gov-ru"),
]
DNS_BOOT = "local"
DNS_DIRECT = "direct"
DNS_REMOTE = "remote"


def base_rules(tun: str) -> list:
    return [
        {"network": ["udp"], "port": [135, 137, 138, 139, 5353], "action": "reject"},
        {"network": ["udp"], "port": [443], "action": "reject"},
        {"ip_cidr": ["224.0.0.0/3", "ff00::/8"], "action": "reject"},
        {"ip_cidr": [f"{tun}/32"], "action": "reject", "method": "drop"},
        {"network": ["icmp"], "outbound": "direct"},
        {"action": "sniff"},
        {
            "type": "logical", "mode": "or", "action": "hijack-dns",
            "rules": [{"port": [53]}, {"protocol": ["dns"]}],
        },
        {"ip_is_private": True, "outbound": "direct"},
    ]


def tun_inbound(name: str, addr: str) -> dict:
    return {
        "type": "tun", "tag": "tun", "interface_name": name,
        "address": [f"{addr}/30"], "mtu": 9000,
        "auto_route": True, "strict_route": True, "stack": "gvisor",
        "route_exclude_address": [
            "192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12",
            "127.0.0.0/8", "169.254.0.0/16",
        ],
    }


def cache_block(path: str, clash_port: int) -> dict:
    return {
        "experimental": {
            "cache_file": {"enabled": True, "path": path, "store_dns": True},
            "clash_api": {"external_controller": f"127.0.0.1:{clash_port}"},
        },
    }


def build_vpn(port=10808) -> dict:
    rules = base_rules("172.18.0.1")
    rule_sets, geosite_tags = [], []
    for fname, tag in BUNDLED:
        p = CACHE / fname
        if p.exists() and p.stat().st_size > 32:
            rule_sets.append({"tag": tag, "type": "local", "format": "binary", "path": str(p.resolve())})
            geosite_tags.append(tag)

    if geosite_tags:
        rules.append({"rule_set": geosite_tags, "outbound": "direct"})
    rules.append({"domain_suffix": RU_TLDS, "outbound": "direct"})
    rules.append({"action": "resolve", "ip_version": 4})

    dns_rules = []
    if geosite_tags:
        dns_rules.append({"rule_set": geosite_tags, "server": DNS_DIRECT})
    dns_rules.append({"domain_suffix": RU_TLDS, "server": DNS_DIRECT})

    route = {
        "auto_detect_interface": True,
        "default_domain_resolver": {"server": DNS_DIRECT, "strategy": "ipv4_only"},
        "final": "proxy",
        "rules": rules,
    }
    if rule_sets:
        route["rule_set"] = rule_sets

    cfg = {
        "log": {"level": "warn"},
        "dns": {
            "servers": [
                {"tag": DNS_BOOT, "type": "local"},
                {"tag": DNS_DIRECT, "type": "udp", "server": "77.88.8.8", "domain_resolver": DNS_BOOT},
                {"tag": DNS_REMOTE, "type": "udp", "server": "1.1.1.1", "detour": "proxy", "domain_resolver": DNS_BOOT},
            ],
            "rules": dns_rules,
            "final": DNS_REMOTE,
            "strategy": "ipv4_only",
            "optimistic": True,
        },
        "inbounds": [tun_inbound("singbox_tun", "172.18.0.1")],
        "outbounds": [
            {
                "type": "socks", "tag": "proxy", "server": "127.0.0.1", "server_port": port, "version": "5",
                "domain_resolver": {"server": DNS_REMOTE, "strategy": "ipv4_only"},
            },
            {
                "type": "direct", "tag": "direct",
                "domain_resolver": {"server": DNS_DIRECT, "strategy": "ipv4_only"},
            },
        ],
        "route": route,
    }
    cfg.update(cache_block(str((CACHE / "singbox-vpn-cache.db").resolve()).replace("\\", "/"), 19090))
    return cfg


def build_bypass(wan="eth0", wg="ByPassMe-Bypass") -> dict:
    domains = json.loads((ASSETS / "bypass-direct-domains.json").read_text())
    rules = base_rules("172.19.0.1")
    if domains:
        rules.append({"domain": domains, "outbound": "direct-wan"})
    dns_rules = []
    if domains:
        dns_rules.append({"domain": domains, "server": DNS_BOOT})

    cfg = {
        "log": {"level": "warn"},
        "dns": {
            "servers": [{"tag": DNS_BOOT, "type": "local"}],
            "rules": dns_rules,
            "final": DNS_BOOT,
            "strategy": "ipv4_only",
            "optimistic": True,
        },
        "inbounds": [tun_inbound("bypass_tun", "172.19.0.1")],
        "outbounds": [
            {"type": "direct", "tag": "direct",
             "domain_resolver": {"server": DNS_BOOT, "strategy": "ipv4_only"}},
            {"type": "direct", "tag": "direct-wan", "bind_interface": wan,
             "domain_resolver": {"server": DNS_BOOT, "strategy": "ipv4_only"}},
            {"type": "direct", "tag": "direct-wg", "bind_interface": wg,
             "domain_resolver": {"server": DNS_BOOT, "strategy": "ipv4_only"}},
        ],
        "route": {
            "auto_detect_interface": True,
            "default_domain_resolver": {"server": DNS_BOOT, "strategy": "ipv4_only"},
            "final": "direct-wg",
            "rules": rules,
        },
    }
    cfg.update(cache_block(str((CACHE / "singbox-bypass-cache.db").resolve()).replace("\\", "/"), 19091))
    return cfg


if __name__ == "__main__":
    CACHE.mkdir(exist_ok=True)
    (CACHE / "test-vpn-singbox.json").write_text(json.dumps(build_vpn(), indent=2))
    (CACHE / "test-bypass-singbox.json").write_text(json.dumps(build_bypass(), indent=2))
    print("configs generated")

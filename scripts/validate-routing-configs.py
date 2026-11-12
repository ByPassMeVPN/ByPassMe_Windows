#!/usr/bin/env python3
"""Проверка split-маршрутизации: логика + структура sing-box как v2rayN."""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "ByPassMe" / "Assets"
CACHE = ROOT / ".cache"

RU_TLDS = (".ru", ".su", ".xn--p1ai")
VPN_RU_SAMPLES = [
    ("yandex.ru", "direct"),
    ("gosuslugi.ru", "direct"),
    ("vk.com", "direct"),
    ("mail.ru", "direct"),
    ("google.com", "proxy"),
    ("youtube.com", "proxy"),
    ("ggsel.io", "proxy"),
    # Сайты проверки IP — всегда через VPN, даже в зоне .ru (иначе покажут IP провайдера)
    ("2ip.ru", "proxy"),
]

# Домены «всегда через VPN» — из общего списка (ByPassMe/Helpers/XrayRoutingLists.cs)
FORCE_VPN = ("2ip.ru", "2ip.io", "2ip.me", "myip.ru", "check-host.net", "whoer.net")
BYPASS_IN = ["vk.com", "ozon.ru", "yandex.ru", "wildberries.ru"]
BYPASS_OUT = ["google.com", "youtube.com", "twitter.com"]


def load_bypass() -> set[str]:
    return set(json.loads((ASSETS / "bypass-direct-domains.json").read_text()))


def vpn_route(domain: str) -> str:
    d = domain.lower()
    if d in FORCE_VPN or any(d.endswith("." + f) for f in FORCE_VPN):
        return "proxy"
    if d.endswith(RU_TLDS):
        return "direct"
    if d in {"vk.com", "yandex.com", "ok.ru", "mail.ru"}:
        return "direct"
    return "proxy"


def bypass_route(domain: str, wl: set[str]) -> str:
    """Обход: домен из списка идёт мимо туннеля, остальное — в туннель."""
    d = domain.lower()
    for w in wl:
        if d == w or d.endswith("." + w):
            return "direct"
    return "tunnel"


def rule_index(rules: list, predicate) -> int | None:
    for i, r in enumerate(rules):
        if predicate(r):
            return i
    return None


def validate_vpn_singbox(cfg: dict) -> list[str]:
    errs: list[str] = []
    route = cfg["route"]
    rules = route["rules"]
    dns = cfg["dns"]

    if route.get("final") != "proxy":
        errs.append("VPN route.final != proxy")
    if route.get("default_domain_resolver", {}).get("server") != "direct":
        errs.append("VPN default_domain_resolver should be direct (v2rayN direct_dns)")
    if dns.get("final") != "remote":
        errs.append("VPN dns.final should be remote for foreign sites")

    tags = {s["tag"] for s in dns["servers"]}
    for need in ("local", "direct", "remote"):
        if need not in tags:
            errs.append(f"VPN DNS missing server tag: {need}")

    remote = next(s for s in dns["servers"] if s["tag"] == "remote")
    if remote.get("detour") != "proxy":
        errs.append("VPN remote DNS must detour proxy")
    if remote.get("domain_resolver") != "local":
        errs.append("VPN remote DNS needs domain_resolver local")

    direct_dns = next(s for s in dns["servers"] if s["tag"] == "direct")
    if direct_dns.get("server") != "77.88.8.8":
        errs.append("VPN RU DNS should be 77.88.8.8 (Yandex, без proxy)")

    ru_dns = any(
        r.get("server") == "direct" and ("domain_suffix" in r or "rule_set" in r)
        for r in dns.get("rules", [])
    )
    if not ru_dns:
        errs.append("VPN DNS rules missing RU → direct")

    sniff_i = rule_index(rules, lambda r: r.get("action") == "sniff")
    geosite_i = rule_index(rules, lambda r: "rule_set" in r and r.get("outbound") == "direct")
    final_proxy_i = len(rules)
    if sniff_i is None:
        errs.append("VPN missing sniff action")
    elif geosite_i is not None and geosite_i < sniff_i:
        errs.append("VPN geosite rule must be after sniff")

    priv_count = sum(1 for r in rules if r.get("ip_is_private"))
    if priv_count != 1:
        errs.append(f"VPN ip_is_private should appear once, got {priv_count}")

    out_tags = {o["tag"] for o in cfg["outbounds"]}
    if "proxy" not in out_tags or "direct" not in out_tags:
        errs.append("VPN outbounds missing proxy/direct")

    return errs


def main() -> int:
    errs: list[str] = []
    wl = load_bypass()
    print(f"OK bypass whitelist file: {len(wl)} domains")

    for d in BYPASS_IN:
        if d not in wl:
            errs.append(f"whitelist missing {d}")
    for domain, exp in VPN_RU_SAMPLES:
        if vpn_route(domain) != exp:
            errs.append(f"VPN logic {domain}: want {exp}")

    if len(wl) < 200:
        errs.append(f"bypass whitelist too short: {len(wl)}")
    for d in BYPASS_IN:
        if bypass_route(d, wl) != "direct":
            errs.append(f"bypass {d} should be direct")
    for d in BYPASS_OUT:
        if bypass_route(d, wl) != "tunnel":
            errs.append(f"bypass {d} should be tunnel")

    vpn_path = CACHE / "test-vpn-singbox.json"
    if not vpn_path.exists():
        errs.append("run generate-test-configs.py first")
    else:
        vpn_cfg = json.loads(vpn_path.read_text())
        ve = validate_vpn_singbox(vpn_cfg)
        if not ve:
            print("OK VPN sing-box structure (v2rayN parity)")
        errs.extend(ve)

    for e in errs:
        print(f"FAIL {e}")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())

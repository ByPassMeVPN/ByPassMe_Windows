#!/usr/bin/env python3
"""Извлекает домены из ref-other-vpn.json → Assets/bypass-direct-domains.json"""
import json
import sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
src = root / "scripts" / "ref-other-vpn.json"
out = root / "ByPassMe" / "Assets" / "bypass-direct-domains.json"

if not src.exists():
    print(f"Нет {src}", file=sys.stderr)
    sys.exit(1)

data = json.loads(src.read_text(encoding="utf-8"))
domains = []
for rule in data.get("routing", {}).get("rules", []):
    if rule.get("outboundTag") == "direct" and "domain" in rule:
        domains.extend(rule["domain"])

domains = sorted(set(d.strip().lower() for d in domains if d and isinstance(d, str)))
out.write_text(json.dumps(domains, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
print(f"✓ {len(domains)} доменов → {out}")

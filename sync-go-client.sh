#!/usr/bin/env bash
# Синхронизация go_client из Android-репозитория (источник истины)
set -euo pipefail
SRC="/opt/ByPassMe-Android/go_client"
DST="/opt/ByPassMe-Windows/go_client"
rsync -a --delete --exclude='bypassclient.exe' "$SRC/" "$DST/"
echo "✅ go_client синхронизирован из ByPassMe-Android"

#!/usr/bin/env bash
# Cross-compile bypassclient.exe on Linux/macOS (CI)
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
GO_DIR="$PROJECT_DIR/go_client"
OUT="$GO_DIR/bypassclient.exe"

echo "📂 $PROJECT_DIR"
echo "⚙️  Building bypassclient.exe (windows/amd64)..."

cd "$GO_DIR"
GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -ldflags="-s -w" -o "$OUT" .
echo "✅ $OUT"

# hub token injection
HUB_TOKEN="${HUB_MOS_TOKEN:-@HUB_MOS_TOKEN@}"
cat > "$PROJECT_DIR/ByPassMe/HubToken.cs" <<EOF
namespace ByPassMe;

internal static class HubToken
{
    internal const string Mos = "$HUB_TOKEN";
}
EOF
echo "🔑 HubToken.cs updated"

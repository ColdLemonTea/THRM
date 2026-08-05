#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
output="$root/build/avalonia-linux-x64"
cd "$root"

command -v dotnet >/dev/null || { echo "dotnet SDK 10 is required." >&2; exit 1; }
command -v go >/dev/null || { echo "Go is required." >&2; exit 1; }
dotnet --list-sdks | grep -q '^10\.' || { echo "dotnet SDK 10 is required." >&2; exit 1; }

dotnet publish gui/THRM.Avalonia/THRM.Avalonia.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --output "$output"

CGO_ENABLED=1 go build -trimpath -o "$output/thrm-core" ./cmd/core
chmod +x "$output/thrm-core" "$output/THRM.Avalonia"

exec "$output/THRM.Avalonia" "$@"

#!/bin/bash
set -e

PROJECT_ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="$PROJECT_ROOT/build"

echo "=== THRM Linux Build ==="

# Resolve build version: THRM_BUILD_VERSION env override -> wails.json productVersion -> "dev".
VERSION="${THRM_BUILD_VERSION:-}"
if [ -z "$VERSION" ]; then
    VERSION=$(grep -oE '"productVersion"[[:space:]]*:[[:space:]]*"[^"]+"' "$PROJECT_ROOT/wails.json" | sed -E 's/.*"([^"]+)"$/\1/')
fi
if [ -z "$VERSION" ]; then
    echo "WARNING: could not resolve version from wails.json, using 'dev'"
    VERSION="dev"
fi
echo "Building version: $VERSION"

LDFLAGS="-s -w -X github.com/TIANLI0/THRM/internal/version.BuildVersion=$VERSION"

mkdir -p "$BUILD_DIR"

# 1. Build core service (requires CGO for go-hid)
echo "--- Building core service ---"
CGO_ENABLED=1 go build \
    -trimpath \
    -ldflags="$LDFLAGS" \
    -o "$BUILD_DIR/thrm-core" \
    ./cmd/core/

# 2. Build Flutter GUI bundle
echo "--- Building Flutter GUI ---"
cd "$PROJECT_ROOT/ui"
flutter pub get
flutter build linux --release --dart-define=THRM_VERSION="$VERSION"

echo "=== Build complete ==="
echo "Binaries:"
ls -lh "$PROJECT_ROOT/ui/build/linux/x64/release/bundle/thrm" "$BUILD_DIR/thrm-core"

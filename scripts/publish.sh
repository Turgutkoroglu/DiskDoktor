#!/usr/bin/env bash
# Builds self-contained single-file DiskDoktor for every supported platform into ./dist.
# Usage: scripts/publish.sh [rid ...]   (default: all)
set -euo pipefail
cd "$(dirname "$0")/.."

RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)

for rid in "${RIDS[@]}"; do
  echo "==> $rid"
  dotnet publish src/DiskDoktor.App/DiskDoktor.App.csproj -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -o "dist/$rid"
done
echo "Bitti: dist/"

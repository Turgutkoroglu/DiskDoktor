#!/usr/bin/env bash
# Builds self-contained single-file DiskDoktor and packages it per platform:
#   dist/<rid>/                      raw publish output
#   dist/release/DiskDoktor-<rid>.*  ready-to-share archive (Windows .zip, Linux .tar.gz, macOS .app in .zip)
# Usage: scripts/publish.sh [rid ...]   (default: all)
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
ASSETS=src/DiskDoktor.App/Assets
OUT=dist/release
RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
mkdir -p "$OUT"

for rid in "${RIDS[@]}"; do
  echo "==> $rid"
  rm -rf "dist/$rid"
  dotnet publish src/DiskDoktor.App/DiskDoktor.App.csproj -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -o "dist/$rid"

  stage=$(mktemp -d)
  case "$rid" in
    win-*)
      mkdir "$stage/DiskDoktor"
      cp "dist/$rid/DiskDoktor.exe" "$stage/DiskDoktor/"
      cp README.md "$stage/DiskDoktor/BENIOKU.md"
      (cd "$stage" && zip -qr "$OLDPWD/$OUT/DiskDoktor-$rid.zip" DiskDoktor)
      ;;
    linux-*)
      mkdir "$stage/DiskDoktor"
      install -m 755 "dist/$rid/DiskDoktor" "$stage/DiskDoktor/DiskDoktor"
      install -m 755 packaging/linux/install.sh "$stage/DiskDoktor/install.sh"
      cp "$ASSETS/diskdoktor.png" "$stage/DiskDoktor/"
      cp README.md "$stage/DiskDoktor/BENIOKU.md"
      tar -C "$stage" -czf "$OUT/DiskDoktor-$rid.tar.gz" DiskDoktor
      ;;
    osx-*)
      app="$stage/DiskDoktor.app/Contents"
      mkdir -p "$app/MacOS" "$app/Resources"
      install -m 755 "dist/$rid/DiskDoktor" "$app/MacOS/DiskDoktor"
      cp "$ASSETS/diskdoktor.icns" "$app/Resources/"
      sed "s/@VERSION@/$VERSION/g" packaging/macos/Info.plist > "$app/Info.plist"
      (cd "$stage" && zip -qry "$OLDPWD/$OUT/DiskDoktor-$rid.zip" DiskDoktor.app)
      ;;
  esac
  rm -rf "$stage"
done
ls -lh "$OUT"

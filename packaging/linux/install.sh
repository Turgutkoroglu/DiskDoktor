#!/usr/bin/env bash
# Installs DiskDoktor for the current user (no sudo): binary, icon and menu entry.
# Run from the extracted release folder:  ./install.sh      Remove with:  ./install.sh --kaldir
set -euo pipefail
cd "$(dirname "$0")"

BIN="$HOME/.local/bin"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

if [ "${1:-}" = "--kaldir" ] || [ "${1:-}" = "--uninstall" ]; then
  rm -f "$BIN/DiskDoktor" "$APPS/diskdoktor.desktop" "$ICONS/diskdoktor.png"
  command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" || true
  echo "DiskDoktor kaldırıldı. Ayarlar ve günlükler: ~/.local/state/DiskDoktor (elle silebilirsiniz)."
  exit 0
fi

[ -f DiskDoktor ] || { echo "DiskDoktor dosyası bu klasörde yok. Arşivi açtığınız klasörde çalıştırın." >&2; exit 1; }

mkdir -p "$BIN" "$APPS" "$ICONS"
install -m 755 DiskDoktor "$BIN/DiskDoktor"
install -m 644 diskdoktor.png "$ICONS/diskdoktor.png"
cat > "$APPS/diskdoktor.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=DiskDoktor
Comment=Güvenli disk temizleyici
Exec=$BIN/DiskDoktor
Icon=diskdoktor
Terminal=false
Categories=System;Utility;
Keywords=disk;temizlik;önbellek;cache;clean;
EOF
command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "${ICONS%/256x256/apps}" 2>/dev/null || true

echo "Kuruldu. Uygulama menüsünde 'DiskDoktor' olarak görünür; terminalden: $BIN/DiskDoktor"

#!/usr/bin/env bash
# LabSign for Linux: per-user install, no root needed.
#
#   ./labsign-linux-install.sh              install or update, then start LabSign
#   ./labsign-linux-install.sh --uninstall  stop it and remove it
#
# LabSign registers its own labsign:// link handler and start-at-login entry each time it
# starts (~/.local/share/applications, ~/.config/autostart), so this only puts the binary in
# place and starts it. Settings and the log live in ~/.config/labsign.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
dest="${XDG_DATA_HOME:-$HOME/.local/share}/labsign"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications/labsign.desktop"
autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/labsign.desktop"

stop_running() {
  pkill -x LabSign 2>/dev/null || return 0
  for _ in 1 2 3 4 5; do
    pgrep -x LabSign >/dev/null || return 0
    sleep 1
  done
  pkill -9 -x LabSign 2>/dev/null || true
}

if [[ "${1:-}" == "--uninstall" ]]; then
  stop_running
  rm -rf "$dest"
  rm -f "$apps" "$autostart" "$autostart.disabled"
  command -v update-desktop-database >/dev/null && update-desktop-database "$(dirname "$apps")" || true
  echo "LabSign removed. Settings are kept in ${XDG_CONFIG_HOME:-$HOME/.config}/labsign."
  exit 0
fi

if [[ ! -f "$here/LabSign" ]]; then
  echo "LabSign binary not found next to this script ($here)." >&2
  exit 1
fi

stop_running
mkdir -p "$dest"
install -m 0755 "$here/LabSign" "$dest/LabSign"

found=""
for lib in /usr/lib/libeToken.so /usr/lib64/libeToken.so /usr/lib/x86_64-linux-gnu/libeToken.so /usr/local/lib/libeToken.so; do
  [[ -e "$lib" ]] && found="$lib" && break
done
if [[ -z "$found" ]]; then
  echo "Warning: SafeNet Authentication Client (libeToken.so) was not found."
  echo "         Install it before signing; LabSign will report the token as missing until then."
fi

nohup "$dest/LabSign" >/dev/null 2>&1 &
disown || true

echo "LabSign installed in $dest and started."
echo "It starts at login and shows a shield icon in the system tray (GNOME needs the AppIndicator extension)."

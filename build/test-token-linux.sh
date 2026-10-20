#!/usr/bin/env bash
# A fake SafeNet token for trying LabSign on Linux without the USB stick.
#
# Creates a SoftHSM2 software token "labsign-test" (user PIN 1234) holding a test RSA key and
# a self-signed certificate "CN=LabSign Test Signatory", and points LabSign at it. Signatures
# made with it are real PDF signatures, just from a test certificate nobody trusts.
#
#   ./test-token-linux.sh            set it up (needs: softhsm2, opensc, openssl)
#   ./test-token-linux.sh --unplug   simulate pulling the stick out
#   ./test-token-linux.sh --plug     simulate plugging it back in
#   ./test-token-linux.sh --remove   delete the test token and point LabSign back at SafeNet
#
# Ubuntu/Debian: sudo apt install softhsm2 opensc openssl
set -euo pipefail

label="labsign-test"
pin="1234"
so_pin="5678"
conf_dir="${XDG_CONFIG_HOME:-$HOME/.config}/labsign"
tokens_dir="$conf_dir/softhsm-tokens"
softhsm_conf="$conf_dir/softhsm2.conf"
labsign_conf="$conf_dir/config.json"

module=""
for candidate in /usr/lib/softhsm/libsofthsm2.so /usr/lib/x86_64-linux-gnu/softhsm/libsofthsm2.so /usr/lib64/pkcs11/libsofthsm2.so /usr/local/lib/softhsm/libsofthsm2.so; do
  [[ -e "$candidate" ]] && module="$candidate" && break
done

set_module() {
  # Rewrites pkcs11_library_path in LabSign's config.json (created on LabSign's first start).
  local path="$1"
  if [[ ! -f "$labsign_conf" ]]; then
    mkdir -p "$conf_dir"
    printf '{\n  "pkcs11_library_path": "%s"\n}\n' "$path" > "$labsign_conf"
    return
  fi
  python3 - "$labsign_conf" "$path" <<'PY'
import json, sys
conf_path, module = sys.argv[1], sys.argv[2]
with open(conf_path) as fh:
    conf = json.load(fh)
conf["pkcs11_library_path"] = module
with open(conf_path, "w") as fh:
    json.dump(conf, fh, indent=2)
PY
}

restart_labsign() {
  if pgrep -x LabSign >/dev/null; then
    pkill -x LabSign || true
    sleep 2
  fi
  local exe="${XDG_DATA_HOME:-$HOME/.local/share}/labsign/LabSign"
  if [[ -x "$exe" ]]; then
    SOFTHSM2_CONF="$softhsm_conf" nohup "$exe" >/dev/null 2>&1 &
    disown || true
    echo "LabSign restarted."
  else
    echo "Start LabSign yourself (it was not found at $exe)."
  fi
}

# Simulated stick: the token's files moved out of SoftHSM's sight and back. LabSign sees the
# change on its next look (the web page polls every 5 s).
if [[ "${1:-}" == "--unplug" ]]; then
  [[ -d "$tokens_dir" ]] || { echo "No test token here; run $0 first." >&2; exit 1; }
  rm -rf "$tokens_dir.unplugged"
  mv "$tokens_dir" "$tokens_dir.unplugged"
  mkdir -p "$tokens_dir"
  echo "Test token unplugged."
  exit 0
fi
if [[ "${1:-}" == "--plug" ]]; then
  [[ -d "$tokens_dir.unplugged" ]] || { echo "The test token is not unplugged." >&2; exit 1; }
  rm -rf "$tokens_dir"
  mv "$tokens_dir.unplugged" "$tokens_dir"
  echo "Test token plugged in."
  exit 0
fi

if [[ "${1:-}" == "--remove" ]]; then
  rm -rf "$tokens_dir" "$tokens_dir.unplugged" "$softhsm_conf"
  set_module "/usr/lib/libeToken.so"
  sed -i '/SOFTHSM2_CONF/d' "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/labsign.desktop" 2>/dev/null || true
  restart_labsign
  echo "Test token removed; LabSign uses SafeNet's libeToken.so again."
  exit 0
fi

for tool in softhsm2-util pkcs11-tool openssl python3; do
  command -v "$tool" >/dev/null || { echo "Missing $tool. Install: sudo apt install softhsm2 opensc openssl" >&2; exit 1; }
done
[[ -n "$module" ]] || { echo "libsofthsm2.so not found; is softhsm2 installed?" >&2; exit 1; }

mkdir -p "$tokens_dir"
printf 'directories.tokendir = %s\nobjectstore.backend = file\n' "$tokens_dir" > "$softhsm_conf"
export SOFTHSM2_CONF="$softhsm_conf"

if softhsm2-util --show-slots 2>/dev/null | grep -q "Label:.*$label"; then
  echo "Test token '$label' already exists."
else
  softhsm2-util --init-token --free --label "$label" --pin "$pin" --so-pin "$so_pin" >/dev/null
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' EXIT
  openssl req -x509 -newkey rsa:2048 -nodes -days 730 -subj "/CN=LabSign Test Signatory/O=Test only" \
    -keyout "$work/key.pem" -out "$work/cert.pem" 2>/dev/null
  openssl pkcs8 -topk8 -nocrypt -in "$work/key.pem" -outform DER -out "$work/key.der"
  openssl x509 -in "$work/cert.pem" -outform DER -out "$work/cert.der"
  openssl rsa -in "$work/key.pem" -pubout -outform DER -out "$work/pub.der" 2>/dev/null
  common=(--module "$module" --token-label "$label" --login --pin "$pin" --id 01 --label "LabSign test")
  pkcs11-tool "${common[@]}" --write-object "$work/key.der" --type privkey >/dev/null
  pkcs11-tool "${common[@]}" --write-object "$work/pub.der" --type pubkey >/dev/null
  pkcs11-tool "${common[@]}" --write-object "$work/cert.der" --type cert >/dev/null
  echo "Created test token '$label' (PIN $pin) with CN=LabSign Test Signatory."
fi

set_module "$module"
# LabSign must see SOFTHSM2_CONF to find the token: add it to the autostart entry too.
autostart="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/labsign.desktop"
if [[ -f "$autostart" ]] && ! grep -q SOFTHSM2_CONF "$autostart"; then
  sed -i "s|^Exec=|Exec=env SOFTHSM2_CONF=$softhsm_conf |" "$autostart"
fi
restart_labsign
echo "LabSign now signs with the test token. Undo with: $0 --remove"

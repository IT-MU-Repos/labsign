#!/usr/bin/env bash
# Publishes both builds into out/release/ (no local SDK needed):
#   LabSign.exe                     Windows, single self-contained file
#   labsign-linux-x64.tar.gz        Linux: LabSign + labsign-linux-install.sh
# plus a .sha256 next to each. CI runs the same steps.
set -euo pipefail
cd "$(dirname "$0")/.."

user_args=(--user "$(id -u):$(id -g)")
[[ -n "${CI:-}" ]] && user_args=()
mkdir -p "$HOME/.cache/labsign-nuget"

docker run --rm "${user_args[@]}" -e HOME=/tmp/home -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -e NUGET_PACKAGES=/nuget -v "$HOME/.cache/labsign-nuget":/nuget -v "$PWD":/src -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 bash -euo pipefail -c '
    mkdir -p "$HOME"
    rm -rf out/release out/linux-x64 out/win-x64
    for rid in win-x64 linux-x64; do
      dotnet publish src/LabSign.Desktop/LabSign.Desktop.csproj -c Release -r "$rid" -o "out/$rid" -p:DebugType=None
    done
    mkdir -p out/release out/pkg/labsign
    cp out/win-x64/LabSign.exe out/release/
    cp out/linux-x64/LabSign build/labsign-linux-install.sh out/pkg/labsign/
    tar -C out/pkg -czf out/release/labsign-linux-x64.tar.gz labsign
    rm -rf out/pkg
    cd out/release
    for f in LabSign.exe labsign-linux-x64.tar.gz; do sha256sum "$f" > "$f.sha256"; done
    ls -la
  '

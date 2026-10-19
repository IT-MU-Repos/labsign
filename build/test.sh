#!/usr/bin/env bash
# Runs the test suite, including the SoftHSM2 token tests, in a .NET 8 SDK image.
# No local SDK needed:  bash build/test.sh   (extra args go to dotnet test)
set -euo pipefail
cd "$(dirname "$0")/.."

docker build -q -t labsign-test -f build/Dockerfile.test build >/dev/null
mkdir -p "$HOME/.cache/labsign-nuget"

# Locally run as yourself so bin/ and obj/ are not root-owned. On a CI runner its uid cannot
# create the SDK's named mutexes in the image, and ownership does not matter there.
user_args=(--user "$(id -u):$(id -g)")
[[ -n "${CI:-}" ]] && user_args=()

docker run --rm "${user_args[@]}" -e HOME=/tmp/home -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e NUGET_PACKAGES=/nuget -v "$HOME/.cache/labsign-nuget":/nuget -v "$PWD":/src -w /src \
  labsign-test bash -euo pipefail -c '
    mkdir -p "$HOME"
    export DOTNET_NOLOGO=1
    export SOFTHSM2_CONF=/tmp/softhsm2.conf
    mkdir -p /tmp/softhsm-tokens
    printf "directories.tokendir = /tmp/softhsm-tokens\nobjectstore.backend = file\n" > "$SOFTHSM2_CONF"
    softhsm2-util --init-token --free --label labsign-test --pin 1234 --so-pin 5678 >/dev/null
    export LABSIGN_SOFTHSM_LIB=$(find /usr/lib -name libsofthsm2.so | head -1)
    dotnet test tests/LabSign.Tests/LabSign.Tests.csproj -c Release "$@"
  ' bash "$@"

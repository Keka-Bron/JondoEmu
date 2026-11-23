#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
TARGET=3.8.1.2
CLIENT_INSTALL="$ROOT/../client/18-11-2026"
PUBLISH_DIR="$ROOT/host-artifacts/beta-server/$TARGET"
JONDO_EMULATOR_ROOT=${JONDO_EMULATOR_ROOT:-"$ROOT/host-artifacts/beta-data/$TARGET"}
if [ ! -f "$JONDO_EMULATOR_ROOT/.jondo-root" ] || [ ! -f "$JONDO_EMULATOR_ROOT/beta-assets.json" ]; then
    printf '%s\n' "No staged November assets at $JONDO_EMULATOR_ROOT. See docs/beta-assets.md." >&2
    exit 1
fi
export JONDO_EMULATOR_ROOT
if [ -z "${JONDO_CLIENT_EXECUTABLE:-}" ] && [ -x "$CLIENT_INSTALL/Dofus.x64" ]; then
    export JONDO_CLIENT_EXECUTABLE="$CLIENT_INSTALL/Dofus.x64"
fi
cd "$ROOT"

# Keep local build inputs independent of the Git revision, in a profile-specific cache.
dotnet publish Jondo.Unity.Server/Jondo.Unity.Server.csproj \
    -c Release \
    -r linux-x64 \
    -p:SkipTests=true \
    -p:SkipRootDeploy=true \
    -p:JondoProtocolTarget="$TARGET" \
    -p:IncludeSourceRevisionInInformationalVersion=false \
    -p:EnableSourceLink=false \
    --artifacts-path "$ROOT/host-artifacts/launcher-local" \
    --output "$PUBLISH_DIR" \
    --nologo

exec "$PUBLISH_DIR/Jondo Server" "$@"

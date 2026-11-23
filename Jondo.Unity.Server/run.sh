#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
ARTIFACTS_DIR="$ROOT/host-artifacts/launcher-local"
cd "$ROOT"

# Share the launcher cache without letting Git revision metadata invalidate it.
dotnet publish Jondo.Unity.Server/Jondo.Unity.Server.csproj \
    -c Release \
    -r linux-x64 \
    -p:SkipTests=true \
    -p:IncludeSourceRevisionInInformationalVersion=false \
    -p:EnableSourceLink=false \
    --artifacts-path "$ARTIFACTS_DIR" \
    --nologo

exec "$ARTIFACTS_DIR/publish/Jondo.Unity.Server/release_linux-x64/Jondo Server" "$@"

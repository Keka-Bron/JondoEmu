#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$ROOT"

# MSBuild reuses up-to-date outputs and rebuilds changed sources.
dotnet publish Jondo.Unity.Server/Jondo.Unity.Server.csproj \
    -c Release \
    -r linux-x64 \
    -p:SkipTests=true \
    --artifacts-path "$ROOT/host-artifacts" \
    --nologo

exec "$ROOT/host-artifacts/publish/Jondo.Unity.Server/release_linux-x64/Jondo Server" "$@"

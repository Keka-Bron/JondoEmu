# syntax=docker/dockerfile:1
#
# Jondo Server, headless, for Linux containers. See docs/docker.md.
#
#   docker compose up -d --build
#   docker build --build-arg SKIP_TESTS=false .   # the same test gate as a normal publish

ARG DOTNET_VERSION=10.0

# ─── Build ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# The project files first, so the NuGet restore is a cached layer that only changes with them.
COPY Directory.Build.props ./
COPY Jondo.Unity.Core/Jondo.Unity.Core.csproj Jondo.Unity.Core/
COPY Jondo.Unity.Contract/Jondo.Unity.Contract.csproj Jondo.Unity.Contract/
COPY Jondo.Unity.Protocol/Jondo.Unity.Protocol.csproj Jondo.Unity.Protocol/
COPY Jondo.Unity.Protocol.Connection/Jondo.Unity.Protocol.Connection.csproj Jondo.Unity.Protocol.Connection/
COPY Jondo.Unity.Protocol.Connection.Wire/Jondo.Unity.Protocol.Connection.Wire.csproj Jondo.Unity.Protocol.Connection.Wire/
COPY Jondo.Unity.Protocol.Game/Jondo.Unity.Protocol.Game.csproj Jondo.Unity.Protocol.Game/
COPY Jondo.Unity.Protocol.Game.Wire/Jondo.Unity.Protocol.Game.Wire.csproj Jondo.Unity.Protocol.Game.Wire/
COPY Jondo.Unity.ProtocolGenerator/Jondo.Unity.ProtocolGenerator.csproj Jondo.Unity.ProtocolGenerator/
COPY Jondo.Unity.World/Jondo.Unity.World.csproj Jondo.Unity.World/
COPY Jondo.Unity.Sprites/Jondo.Unity.Sprites.csproj Jondo.Unity.Sprites/
COPY Jondo.Unity.Server/Jondo.Unity.Server.csproj Jondo.Unity.Server/
RUN dotnet restore Jondo.Unity.Server/Jondo.Unity.Server.csproj -r linux-x64

COPY . .

# true: publish only. false: run the general, protocol and server suites first (the
# RunTestsBeforePublish target), which need bases/world.db unpacked from datos/world.zip.
ARG SKIP_TESTS=true
RUN if [ "$SKIP_TESTS" != "true" ]; then \
        apt-get update && apt-get install -y --no-install-recommends unzip && \
        mkdir -p bases && unzip -o -q datos/world.zip -d bases; \
    fi \
 && dotnet publish Jondo.Unity.Server/Jondo.Unity.Server.csproj \
        -c Release -r linux-x64 \
        -p:SkipTests=$SKIP_TESTS -p:SkipRootDeploy=true \
        --artifacts-path /artifacts --nologo

# ─── Runtime ──────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# The data root: the marker, the read-only data and the server.
COPY --from=build /src/.jondo-root ./
COPY --from=build /src/datos ./datos
COPY --from=build /src/content ./content
COPY --from=build /src/mods ./mods
COPY --from=build ["/artifacts/publish/Jondo.Unity.Server/release_linux-x64/Jondo Server", "./"]

# Everything the server writes lives in these three folders, each a volume. Created here and owned
# by the image's non-root user so that fresh named volumes start with the same owner.
RUN mkdir -p bases config logs /tmp/dotnet-bundle \
 && chown -R app:app bases config logs /tmp/dotnet-bundle

ENV JONDO_EMULATOR_ROOT=/app \
    JONDO_PUBLIC_BIND=1 \
    JONDO_RESTART_MODE=exit \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/dotnet-bundle

VOLUME ["/app/bases", "/app/config", "/app/logs"]

# 5555 game and connection, 5556 game node compatibility, 6337 chat, 8888 HAAPI and control API,
# 15881 Zaap.
EXPOSE 5555 5556 6337 8888 15881

USER app

# The first start unpacks world.db (about 240 MB) before any port opens: hence the long start period.
HEALTHCHECK --interval=30s --timeout=5s --start-period=180s --retries=3 \
    CMD ["bash", "-c", "</dev/tcp/127.0.0.1/8888"]

ENTRYPOINT ["/app/Jondo Server", "--headless"]

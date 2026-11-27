# Jondo Server in Docker

The server can run as a headless Linux container. This is **an extra way to run it**: the Windows
and Linux executables, the launcher starting `Jondo Server.exe` itself and `run.sh` work exactly as
before. Nothing changes unless you start the container.

## Quick start

```sh
cp .env.example .env          # optional: bind address, tag, time zone
docker compose up -d --build
docker compose logs -f        # wait for "ALL EMULATION SERVICES ONLINE AND READY!"
docker compose ps             # STATUS shows (healthy)
```

The first start unpacks `datos/world.zip` into the `bases` volume (about 240 MB) and creates
`auth.db` with the test account. Later starts reuse it.

> ⚠️ The test account `keka` / `test` is an administrator and its password is public. Change it or
> delete it before players other than you can reach the server.

Stop with `docker compose stop` (SIGTERM: players are told, the world is saved, exit 0). `docker
compose down` also removes the container but **keeps the volumes**; `down -v` deletes them, with
every character and account.

## Playing with the server in Docker

The launcher, the Dofus client and the mods stay on Windows as usual
([Quick Start](../README.md#-quick-start), steps 1–3). Only the server moves into the container.

### On the Docker host

1. Start Docker Desktop (or the Docker engine).
2. In the emulator folder, start the server and wait until it is healthy:
   ```sh
   docker compose up -d --build
   docker compose ps            # STATUS: (healthy) — about a minute on the first start
   ```
3. Get the launcher. A release zip already has `Jondo Emulator Launcher.exe`; in a source checkout,
   publish it once, which copies it to the emulator root:
   ```sh
   dotnet publish Jondo.Unity.Launcher/Jondo.Unity.Launcher.csproj -c Release -r win-x64
   ```
4. Leave `servidor=` empty or absent in `%APPDATA%\Jondo\lanzador.cfg`.
5. Double-click `Jondo Emulator Launcher.exe`. It finds the server already answering on
   `127.0.0.1:8888` and uses it ("There is a server running; attaching to it.") instead of
   starting `Jondo Server.exe`.
6. Sign in (`keka` / `test` on a fresh volume), tick a profile and press **Launch selected**.

Closing the launcher leaves the server running; `docker compose stop` ends it.

The container has its **own** databases, in the `jondo-bases` volume, started from `world.zip`. It
does not see the native installation's `bases/` folder, so characters made in one do not appear in
the other.

### From another PC

1. On the Docker host, allow 5555, 6337, 8888 and 15881 through the firewall, and change or delete
   the `keka` account.
2. On the player's PC, put the Docker host's LAN or VPN address in `lanzador.cfg`:
   ```text
   servidor=192.168.1.10
   ```
3. Start the launcher. It relays the client's loopback traffic to that host
   ([remote server](remote-server.md)) and starts no local server.

### Back to the native server

`docker compose stop`, then start the launcher as before: no server answers, so it starts
`Jondo Server.exe` itself with the native `bases/`. Never run both at once; they need the same ports.

### If the launcher shows the server offline

- `docker compose ps`: the container must be `(healthy)`, not restarting.
- `docker compose logs --tail 50`: the end should read `ALL EMULATION SERVICES ONLINE AND READY!`.
- `curl http://127.0.0.1:8888/` must answer. If it does not, check `JONDO_BIND_IP` in `.env`
  (`0.0.0.0` or `127.0.0.1` for a launcher on the Docker host).
- No native `Jondo Server.exe` is still running and holding the ports.

## What is in the image

| Path | What | Kind |
|---|---|---|
| `/app/Jondo Server` | the server, started with `--headless` | image |
| `/app/.jondo-root`, `/app/datos`, `/app/content` | read-only game data | image |
| `/app/bases` | `world.db`, `auth.db`, `paquetes.db`, `backups/` | volume `jondo-bases` |
| `/app/config` | `server_settings.json` | volume `jondo-config` |
| `/app/logs` | debug, traffic and activity logs | volume `jondo-logs` |

The image runs as the non-root `app` user and sets:

| Variable | Value | Why |
|---|---|---|
| `JONDO_EMULATOR_ROOT` | `/app` | the data root |
| `JONDO_PUBLIC_BIND` | `1` | listen on the container's network, not its own loopback |
| `JONDO_RESTART_MODE` | `exit` | see [Restart](#restart) |
| `DOTNET_BUNDLE_EXTRACT_BASE_DIR` | `/tmp/dotnet-bundle` | writable native-library extraction |

`JONDO_SIN_REGISTRO_DE_TRAFICO`, `JONDO_RAID_MIN_PLAYERS` and `TZ` can be set in `.env`.

## Optional server mods

Put standalone DLLs and unpacked packages in `mods/` on the Docker host and bind-mount
it at `/app/mods`. Include `JondoFix.dll` and any other client DLLs the server should
offer, because this mount replaces the image's built-in `mods/` directory. Add this
to a Compose override file next to `docker-compose.yml`.

```yaml
services:
  jondo-server:
    volumes:
      - ./mods:/app/mods:ro
```

Add the desired DLL filenames and package IDs to `ServerMods` in the container's
`/app/config/server_settings.json`. Apply the new mount once with
`docker compose up -d`. For later changes to the folder or the load order, use
`docker compose restart jondo-server`. See the [server mod installation instructions](server-mod-contract.md#install-and-enable-server-mods).

## Ports and players

| Port | Service |
|---:|---|
| 5555 | connection and game server |
| 6337 | chat |
| 8888 | HAAPI and launcher control API |
| 15881 | Zaap |

5556 (game node compatibility) is exposed by the image but not published by compose; the client
does not use it.

Players connect exactly as to a [remote server](remote-server.md): `servidor=<docker host>` in the
launcher's `lanzador.cfg`. A launcher on the Docker host itself needs nothing: the ports are
published on the host's `127.0.0.1` too. Do not run the native server on the same host and address
at the same time; both want the same ports.

The traffic is **not encrypted**. Publish the ports on a trusted LAN or behind a VPN only, which is
what `JONDO_BIND_IP` in `.env` is for (`127.0.0.1` keeps them on the Docker host).

## Several servers

The ports are fixed in the server and the client is always sent to `127.0.0.1:5555` (the launcher
relays it), so **each instance needs its own host IP**. The compose file has a second, independent
instance (its own volumes, so its own world and accounts) behind a profile:

```sh
# .env: JONDO_BIND_IP=192.168.1.10   JONDO_BIND_IP_2=192.168.1.11
docker compose --profile second up -d
```

## Restart

*Save and Restart* in the settings normally starts a new server process and ends the old one. In a
container the server is PID 1, so that child would die with it. With `JONDO_RESTART_MODE=exit` the
server instead ends with exit code 75 and Docker's `restart: unless-stopped` starts the next run,
with the new settings. Without the variable (every non-Docker installation) nothing changes.

The settings are edited in `config/server_settings.json` inside the `jondo-config` volume, for
example:

```sh
docker compose cp jondo-server:/app/config/server_settings.json .
# edit it, then
docker compose cp server_settings.json jondo-server:/app/config/server_settings.json
docker compose restart jondo-server
```

## Zaap discovery

In `config/server_settings.json`, `"RequireZaapDiscovery": true` (the default) makes ordinary Zaap
destinations available only after the character visits their map. Set it to `false` and restart
to make all usable ordinary Zaaps available immediately. Visits are recorded in both modes, so
switching back to discovery uses the visits each character has already recorded.

## Backups

The server's own backups go to `bases/backups` in the `jondo-bases` volume
([database backups](database-backups.md)). To copy the whole volume out, stop the server first so
SQLite's files are consistent:

```sh
docker compose stop
docker run --rm -v jondoemu_jondo-bases:/data -v "$PWD":/out alpine \
    tar czf /out/jondo-bases.tgz -C /data .
docker compose start
```

(The volume's name is prefixed with the compose project, normally the folder name.)

## Building

```sh
docker compose build                                   # publish only
docker compose build --build-arg SKIP_TESTS=false      # the test gate of a normal publish first
```

The build uses the .NET 10 SDK image and the runtime uses `mcr.microsoft.com/dotnet/aspnet:10.0`,
which carries the ASP.NET Core shared framework the server needs.

## Towards high availability and a full ops deployment

Running the server in Docker is the first step towards high availability and a complete operations
setup. It gives the server what those are built on:

- **One reproducible artifact.** The image is built from the repository and holds the server and
  its game data. The same image runs on a laptop, a VPS or a cluster, and a release can be pushed
  to a registry by CI and pulled by any host.
- **State apart from code.** Everything the server writes is in three volumes, so upgrading or
  rolling back is replacing the image while the data stays. Backing up is copying the volumes.
- **Self-healing.** The healthcheck and `restart: unless-stopped` bring a failed server back on
  their own. Orchestrators (Docker Swarm, Kubernetes, Nomad) read the same signals.
- **Clean lifecycle.** SIGTERM ends it gracefully with players told and the world saved, which is
  what a rolling update or a failover sends.
- **Configuration from the environment.** Bind address, restart mode and the server's switches
  come from variables, which deployment tools set per environment.
- **Observable.** Logs go to stdout for `docker logs` and any log collector, and to the `logs`
  volume.

The next steps are publishing the image from CI, deploying it to a host (compose or an
orchestrator), automated volume backups, monitoring and alerting on the healthcheck, and the
failover described below. A standby node only has to run the same image on the same data.

The world lives in memory and in SQLite files, so **two instances can never share one database**:
SQLite over shared storage with two writers corrupts it. What works today is a single instance
restarted quickly (the healthcheck and restart policy above). Active/passive failover (a standby
started on replicated storage, or the database files replicated with a tool such as Litestream) is
the next step. Every file the server writes is already in the three volumes, which is what such a
setup needs.

### The end goal: one image per service

Today one process, and so one image, plays every role: HAAPI (8888), Zaap (15881), the connection
and game server (5555), the game node (5556) and chat (6337). They share one memory (the session
registry and the live world) and the same two SQLite databases. The goal is to **split the server
into services, each with its own image**, which can be deployed, scaled and restarted on its own:

| Service | Today | As its own image |
|---|---|---|
| Auth and HAAPI | port 8888, `auth.db` | stateless web API, several replicas behind a load balancer |
| Zaap | port 15881 | stateless, scaled freely |
| Connection | port 5555, shared with the game | logs in and hands the player to a world node |
| World / game | port 5555, the in-memory world, `world.db` | one or more world nodes, each owning a part of the world |
| Chat | port 6337 | its own service, scaled apart from the world |

That is a refactor of the server, not a Docker change. It needs:

- **A database server** (PostgreSQL, for instance) instead of SQLite files, so several processes can
  write at once.
- **Services that talk over the network** (a message bus or RPC) instead of sharing memory, for
  sessions, the hand-off from connection to world, and chat.
- **Configurable ports and addresses**, since today they are fixed and the client is always sent
  to `127.0.0.1:5555`.
- **A world split into nodes** (by map area, or one node per instance of a dungeon or fight), with
  a player's state moved between them.

The single image here is the step before that split. Its volumes, healthcheck, environment
configuration and SIGTERM handling are what each future service image will need too.

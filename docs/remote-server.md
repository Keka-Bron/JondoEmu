# Remote server and launcher relay

Jondo can run the server on another Windows or Linux machine while the launcher and Dofus stay on
the player's PC. The launcher creates an in-process TCP relay for traffic addressed to `127.0.0.1`:
JondoFix redirects it on Windows, and the native client configuration selects it on Linux.

Local installations do not change: every server listener remains loopback-only and the launcher
starts no relay unless a remote host is configured.

## Server machine

Set `JONDO_PUBLIC_BIND=1` in the environment that starts `Jondo Server.exe` on Windows or `Jondo Server`
on Linux. The `ServerBinding` switch applies to the server listeners:

| Port | Service |
|---:|---|
| 5555 | connection and game server |
| 5556 | game node compatibility listener |
| 6337 | chat (Windows; unsupported in the native Linux workflow) |
| 8888 | HAAPI and launcher control API |
| 15881 | Zaap TCP, HTTP and WebSocket |

The named pipe also served by Zaap remains local to the server machine; remote clients use its TCP
endpoint through the relay.

On Windows, a non-administrator account may need an HTTP URL reservation for the wildcard HAAPI
listener. The server reports the operating-system error instead of claiming that its services are
online when the bind fails.

Allow the required ports through the server firewall. Port 5556 is not advertised to the normal
client and does not need to be exposed unless a separate compatibility setup uses it.

## Player machine

Set the server in `%APPDATA%\Jondo\lanzador.cfg` on Windows or
`$XDG_CONFIG_HOME/Jondo/lanzador.cfg` on Linux (normally `~/.config/Jondo/lanzador.cfg`):

```text
servidor=server.example.net
```

An IPv4 address can be used instead of a DNS name. On its next start, the launcher listens only on
the player's loopback interface and forwards each connection to the same port on that host:

- Windows: 5555, 5556, 6337, 8888 and 15881.
- Linux: 5555, 5556, 8888 and 15881.

It does not install a Windows service, modify `netsh`, require
administrator rights or leave forwarding rules behind after it exits.

The launcher itself continues to call the control API directly on the configured host. The relay
exists for the client's loopback traffic and runs only while the launcher remains open.

See [Linux desktop setup](linux-desktop.md) for configuring the native client's `ConfigUrl`.

## Transport security

The relay is TCP forwarding, not a VPN: it does **not** add encryption or authenticate the remote
machine. In particular, port 8888 carries launcher login and session data over HTTP. Use the public
bind only on a trusted LAN or behind an encrypted VPN/tunnel, and restrict the server firewall to
the expected client addresses. Do not expose these ports indiscriminately to the public internet.

This limitation is explicit because a working remote connection and a secure public deployment are
different claims. The relay solves address routing; the network owner remains responsible for a
trusted or encrypted path.

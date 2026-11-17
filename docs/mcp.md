# Debugging with an AI agent (Jondo.Unity.Mcp)

`Jondo.Unity.Mcp` is an [MCP](https://modelcontextprotocol.io/) server over stdio. An AI coding
agent (OpenCode, Claude Code...) uses it to drive the emulator while it debugs: start the server it
has just changed, put a character in the world, play it, read what the server logged, and look at
and click the real Dofus client.

It is a developer's tool. The release does not publish it, and everything it uses on the server is
off unless the server was started with `JONDO_TEST_KEY`.

## Three layers

| Layer | What it drives | What the server needs |
| --- | --- | --- |
| Server | `server_start` / `stop` / `restart`, `server_logs`, `admin_call`, `test_call` | The control API on 8888 |
| A character without a client | `headless_connect`, `game_*`, `fight_*`, `headless_send` | `/api/test/*` (JONDO_TEST_KEY) |
| The real client | `client_launch`, `client_screenshot`, `client_click`, `client_ui`... | JondoFix with `JONDO_INPUT_PORT` |

**Server.** `server_start` runs `Jondo.Unity.Server` headless with the run's test key. With
`fromSource` (the default) it uses `dotnet run`, so a change is built and tried in one call. A server
the tools started is killed when the tools exit: a server left behind would hold the ports with a
key nobody has.

**A character without a client.** The `/api/test` routes (`ControlApiTest.cs` and its partials) act
on a test account's character in the world: `jondoplay` for slot 0, `jondoplayNN` for slot n. The
server creates that account the first time, as an administrator with a random password. Until now a
character only got into the world with a real client. `HeadlessClient` (`Bots/`) logs it in over a
socket of its own, as a JondoBot does: a token, the connection server, a ticket, the character list,
the world. It then answers only what a client must answer for the server to go on: the maps it is
put on, `jwz` before every turn, `jti` for every sequence, the heartbeat, and `kaq` in a placement
(`autoReady`). Its turns are played by whoever drives it.

Unlike a JondoBot's, its socket is not quieted, so the server logs it as it logs any player. It also
keeps the last 2,000 frames the server sent it (`headless_frames`). `headless_send` writes any client
message to its socket, so the message goes through the server's whole socket loop. `test_call
inject` hands a message straight to its handler instead.

**The real client.** `client_launch` starts Dofus as the launcher does, for the slot's test account.
The mods come from the source checkout, and `JONDO_INPUT_PORT` is set. On that port JondoFix
(`TestInput.cs`, `TestInterface.cs`) answers one line per command:
- a mouse and a keyboard of its own, so it does not move the computer's pointer and works with the
  window in the background;
- the interface read from UI Toolkit, with every box in window pixels;
- `shot`, a JPEG of the window taken at the end of a frame.

A slot is played either headless or by a client, never both at once.

## Setting it up

Build it:

```sh
dotnet build Jondo.Unity.Mcp
```

Then register `bin/Debug/net10.0/Jondo.Unity.Mcp.dll` with the agent. Run the dll directly: `dotnet
run` writes its build output to stdout, which is the protocol's channel. For OpenCode, in
`opencode.json` at the checkout's root:

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "jondo": {
        "type": "local",
        "command": ["dotnet", "C:/path/to/JondoEmu/Jondo.Unity.Mcp/bin/Debug/net10.0/Jondo.Unity.Mcp.dll"],
        "cwd": "C:/path/to/JondoEmu",
        // Screenshots come back as images: keep the tools on the native list.
        "codemode": false,
        "environment": {
          "JONDO_EMULATOR_ROOT": "C:/path/to/JondoEmu"
        }
      }
    }
  }
}
```

| Variable | Default | What for |
| --- | --- | --- |
| `JONDO_EMULATOR_ROOT` | the folder with `.jondo-root` above the dll | The data the server reads: `bases/`, `content/`, `logs/` |
| `JONDO_MCP_SOURCE` | the data root | The checkout the server is built from and whose mods the client is given. Point it at a worktree to try a branch against the main checkout's data |
| `JONDO_TEST_KEY` | drawn for each run | Set it to use a server started by hand with the same key |
| `JONDO_CLIENT_EXECUTABLE` | as the launcher finds it | The client's `Dofus.exe` |

## A session

```text
server_start                                  built from source, online
character_create breed=8 level=100 name=Mcp-Yopuka
headless_connect character=Mcp-Yopuka         in the world in under a second
fight_team foes=[1]                           against a JondoBot Feca, placement passed
fight_stage spell=13115 target=<bot> as=<me>
fight_cast spell=13115 cell=231 as=<me>       each effect judged, the invariants, the server's lines
server_logs since=<lastId>
```

Then, with the client:

```text
headless_disconnect
client_launch                                 logged in on its own
client_ui filter=jouer                        [{"t":"JOUER","x":695,"y":758,"w":210,"h":99,...}]
client_click x=800 y=807
client_screenshot
```

## Things to know

- **What it writes.** The test account `jondoplay` (and `jondoplayNN`) and its characters are
  written to the data root's databases, like any other account.
- **The test key.** The key is a secret: with it, every test route is open. `Censura` keeps it out of
  the log. `server_logs` also leaves out the lines of the tools' own calls, unless `includeHarness`.
- **The client's mods.** `client_launch` installs the source checkout's `JondoFix.dll`, `Heroes.dll`
  and `Cazacabezas.dll` into the client, as the launcher installs the data root's. The next launch
  from the launcher puts its own back if they differ.
- **Windows only** for the client: the input port is JondoFix's, and JondoFix is a MelonLoader mod.

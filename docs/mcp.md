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
| Server | `server_start` / `stop` / `restart`, `server_logs`, `admin_call`, `player_call`, `test_call`, `run_tests`, `protocol_fields` | The control API on 8888 |
| A character without a client | `headless_connect`, `game_*` (`game_dialogue`), `fight_*`, `headless_send` | `/api/test/*` (JONDO_TEST_KEY) |
| The real client | `client_enter_world`, `client_screenshot`, `client_click`, `client_ui`, `client_set_text`, `client_log`... | JondoFix with `JONDO_INPUT_PORT` |

**Server.** `server_start` runs `Jondo.Unity.Server` headless with the run's test key. With
`fromSource` (the default) it uses `dotnet run`, so a change is built and tried in one call, and it
reads the source checkout's `content/` (`JONDO_CONTENT_ROOT`): a branch's content goes with its code,
while the databases stay the data root's. A server the tools started is killed when the tools exit: a
server left behind would hold the ports with a key nobody has.

`run_tests` runs `Jondo.Unity.Tests` on a checkout (the source, or a worktree's path) with
`JONDO_EMULATOR_ROOT` on it, copies the data root's databases into its `bases/` when it has none, and
builds where the tests look for the repository's files. `protocol_fields` tells what a game message's
fields mean where it has been settled (`docs/protocol_fields.json`): the client's names are rotated,
so a field's meaning is a finding kept with how it was found.

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

**Player control routes.** `player_call` sends JSON to a route under `/api/` with
the test slot's account token. Use it for installed packages' authenticated routes.
To act as an active companion in the test harness, pass `"companion": name or id` to
`test_call`, or use `companion=` with `game_state`, `game_give` or `game_dialogue`.

**Order.** A slot's actions run one after the other, never two at once in its session; what only reads
is not held. But calls an agent sends together reach the tools in any order: steps that depend on one
another go in one call. `game_dialogue npc=... replies=[...]` talks to an NPC and chooses the replies
in order (four replies sent at once ran the second first, and the NPC's conversation closed).

**The real client.** `client_launch` starts Dofus as the launcher does, for the slot's test account.
After the local test server accepts the test key, MCP verifies and activates the server's client manifest. `JONDO_INPUT_PORT` is set. On that port JondoFix
(`TestInput.cs`, `TestInterface.cs`) answers one line per command:
- a mouse and a keyboard of its own, so it does not move the computer's pointer and works with the
  window in the background;
- the interface read from UI Toolkit, with every box in window pixels;
- `shot`, a JPEG of the window taken at the end of a frame, or of a region of it, enlarged;
- `settext`, a text written into a field through the game's own `TextInput` (and validated as Enter
  would with `settext!`): the harness's keyboard does not reach a field in a client in the
  background.

`client_enter_world character=...` does the whole way in: the client started when there is none, the
server's JOUER, the character picked and his JOUER, until the server has him in the world. A client
left on its connection screen by a server restart (its JOUER greyed for good) is started again.
`client_find hidden=true` finds hidden elements too -- the error label a field keeps hidden until its
error style shows it. `client_log` reads MelonLoader's `Latest.log`: what the mods wrote.

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
| `JONDO_MCP_SOURCE` | the data root | The checkout the server is built from, whose `content/` it reads and whose bundled client files it can use. Point it at a worktree to try a branch against the main checkout's data |
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
client_enter_world character=Mcp-Yopuka       started, logged in, picked, in the world (~20 s)
client_set_text element=ti_chatInput text=".item 39 1" submit=true
client_screenshot x=0 y=640 width=290 height=260 zoom=2 savePath=C:/shots/chat.jpg
client_log contains=[JondoFix]
```

## Things to know

- **What it writes.** The test account `jondoplay` (and `jondoplayNN`) and its characters are
  written to the data root's databases, like any other account.
- **The test key.** The key is a secret: with it, every test route is open. `Censura` keeps it out of
  the log. `server_logs` also leaves out the lines of the tools' own calls, unless `includeHarness`.
- **The client's mods.** `client_launch` uses the active server manifest. Older servers without
  a manifest use the checkout’s standalone `mods/` DLLs.
- **Windows only** for the client: the input port is JondoFix's, and JondoFix is a MelonLoader mod.

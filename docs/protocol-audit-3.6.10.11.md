# Protocol audit against the 3.6.10.11 client (2026-10-18)

What the server sends, read against the client's own classes, and what the client sends that the
server does not answer.

## The schema

`datos/protocolo_3.6.10.11.proto` (and `protocolo_conexion_3.6.10.11.proto`) are rebuilt with
`protocolbuilder proto` from the client's Cpp2IL assemblies. The earlier dumps paired each field
number with the class's properties by position, and a proto3 `optional` field brings a getter-only
`Boolean` (whether it is set) behind it: every number after it was shifted (`jug`'s wave came out
as its f5). `ProtoWriter` now counts only the properties a field is written through -- those with
a setter, and the lists and maps -- and the 2,169 messages come out with no mismatch.

## Malformed packets

| Check | Coverage | Result |
|---|---|---|
| Every S->C frame of the traffic logs (Oct 11-18), decoded against the schema (`tools/auditar_protocolo.py`) | 129,809 frames, 297 messages | clean in the latest log; older logs show the `mdu`/`mdx` rank-as-fighter block fixed in 0cbe252 and the `.level` command's `kri`/`krb`/`bcy` |
| Every frame the 2,573 tests write (`JONDO_FRAME_TAP=<file>`, see `Jondo.Unity.Tests/FrameTap.cs`) | 915 frames, 112 messages | clean |
| The builders no frame shows, followed through their `Pb` chains (`tools/auditar_constructores.py`) | 100 of 113 | clean (one packed list written by hand, valid) |
| The 13 builders the script cannot follow, by eye | 13 | clean |

Fixed:

- `.level` sent `kri`, `krb`, `krd` and `bcy`, and `.kamas` sent `bvr`: names of an earlier protocol.
  In 3.6, `krb`, `bcy` and `bvr` do not exist and `kri` is another message whose f1 is an integer,
  so the client could not parse the stats list sent as it. Removed; the `kub` and `ivf` already sent
  are what refresh the sheet and the kamas.

Dead code left in place (no 3.6 client message reaches it): `StatsHandler.HandleStatsUpgradeRequest`
(`krc`, 3.6.4.3), `InventoryHandler.HandleItemMovementRequest` (`isi`, 3.6.4.3), the `kkn`, `joi`,
`jpp`, `hmv`, `ibt`, `igx`, `knx` branches of `GameNodeProxy`, and FightHandler's builders of `jox`,
`jxe`, `jyi`, `jyk`, `jvm`, `jpf`, `jud`, `joo` -- 25 names in all that the client has no class for.

## Client messages the server does not answer

From the console log's unhandled packets, checked against the code, then the real server's answers
in the 756 captures and the client's senders in Ghidra.

| Message | What it is | Real server's answer | State |
|---|---|---|---|
| `jqe` | the walk cut short on a cell (a new click on the way) | nothing | **fixed**: the character stands on that cell, and the cut walk's passage no longer fires |
| `hir` | the zaap trip's cost to a map, asked on arrival and by the autopilot | `him` { map, kamas } | **fixed** |
| `knm`, `kno`, `kny` | the contact lists, asked on entering the world and when the window opens | `kqg` (mutual friends), `koj` (contacts), `koi` (blocked) | **fixed**, with adding (`koq`, `kok`, `kpa`) and removing (`kog`, `kqd`, `kpl`): see `FriendsHandler` |
| `kon` | the friends window's setting { f2 }, as it opens | nothing | **fixed** (taken silently) |
| `hom` | the spouse's information (its answer, `hol`, has the spouse's name, level, look and guild) | nothing, to a player who is not married | **fixed** (taken silently) |
| `ido` | a document's «start the quest» button: a wanted poster, a book (the "se busca" posters' 1563-1565) | the start, or `lqn` 626 «No puedes empezar una misión ya empezada»; `kla` brings the `kld` | **fixed** |
| `idv` | a quest no longer followed { quest, -1 } (`iec` follows one) | nothing | **fixed**: the followed quests come back in `iel` on entering the world |
| `jwm`, `jth` | the shop's gifts window: one gift, or all of them, to a character (listed by `jtg` after the character list) | `jwj` { id, true }, `iua`, `iun` per gift | **fixed**: the gifts an administrator grants from F10 |
| `kwd` | character creation's random name | `kvk` { name } | **fixed**: the branch waited for `kvk`, which only the server sends |
| `koc`, `koe`, `kpb` | requests on entering the world | guild information, or nothing | not needed (sent unasked) |
| `ktn`, `hos`, `lrd`, `jha` | chat channels on entering the world | `hou`, eleven `ktq`, `lru` | not needed (sent unasked) |
| `kmr` | world entry | `kml`, `kmp`, `ktz`, `iom` | not needed (sent unasked) |
| `krv` | world entry, with a request id | `mgz`, `kqp` | left unanswered on purpose |
| `lqf` | the map's load time in ms, after `lqc` | nothing | not needed |
| `ivp` | the average prices of the item tooltips, on entering the world and when the client's copy is old | `ivi` { f2 [ item, price ] } (~9,700 items), to all 140 of the captures' `ivp` | **fixed**: the marketplaces' sales |
| `jlh` | the guild window closing (GuildBaseUI.OnDisable) | nothing | **fixed** (taken silently) |
| `mdw`, `mdt` | the alliance nuggets window opening and closing (NuggetsUi) | `mec` { pot } to `mdw` | **fixed**, with `meb` (handing the pot out) and recycling (`kga`/`kgh`) |
| `med` | swapping places in an alliance fight (JoinFightUtils.SwapPlaces) { f1 the fight, f3 the defender } | unknown | **fixed**, inferred: a join, or the named defender's place when the side is full |
| `iey` | the prism menu's «Añadir un cristal» (PrismMenuMaker.OnPrismModulesManage) | the crystal window, `kci` { f3 18 } (PrismModuleUi), then `kcr` drops the crystal | **fixed**: see `PrismHandler`, the own alliance's prism view (ley f1, f6) |
| `hkp`, `mat` | a tax collector request, an alliance one | nothing reaches them: no window raises their events (drk, dpm) | not needed |
| `kug` | «Resucitar» on the death popup, «Liberar alma» on one's own menu, as a tomb (eoy.bani) | `kup` { 0 } and the way to the save point | **fixed** for a conquest's tombs: see `ConquestHandler` |
| `iwo` on element 488131 | «Consultar» (skill 293) on the board in front of the Temple of Alliances | `iwn`, then `hks` { f1 the world's top, f3 the dungeons' } (TopTaxCollectors) | **fixed**: see `TaxCollectorHandler.ShowTopsAsync` |
| `kjw`, `jaq` | the map panel's «leave the dungeon» and «leave the haven bag» (MapInfoUI) | the way out | **fixed** |
| `hph` without its f2 | «Agredir» on a player (PlayerMenuMaker.OnAttack), which the server took for a challenge | the aggression's fight in a conquest's zone, else `lqn` 421 | **fixed**: see `ConquestHandler`, with the conquest's `mep` and `mdz` |

The Ankama friends -- the window's first section -- are not game messages: they go to the chat
service (`ChatServer`, Spin over TLS on 6337), which only answered the login, with a frame length that left
out its own header and the type byte, so the client hung up at once. It now speaks the transport and the
friends' requests and events.

`kpa` adds a player to the ignored-player list. Native client analysis confirms
this meaning through the context menu and party-invitation UI; see the
[`kpa` evidence](opcodes.md#2131-ignore-player-kpa). It has no reviewed semantic
binding, and its tag-2 enum remains unresolved.

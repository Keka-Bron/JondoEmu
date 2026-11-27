# Cazacabezas and Heroes server-plugin migration

Baseline for the audit is `00e8dfb4` (`modding api start`). The client and server code for each mod ships in its own package. The base server and its publish tests can build without either optional package. Paths are repository-relative.

Extract `cazacabezas-server-mod.zip` or `heroes-server-mod.zip` from the matching
JondoEmu release into `mods/`. Add the extracted package's ID, `cazacabezas`
or `heroes`, to `ServerMods` and restart. See [server mod installation](server-mod-contract.md#install-and-enable-server-mods).

## Shared settings, distribution and authentication

- `Jondo.Unity.Server/ServerSettings.cs` loads `config/server_settings.json` once for the running process. The Mods page discovers standalone DLLs, installed packages, and package settings from manifests. Edits apply after restart. Set `ServerMods` to the DLL filenames and package IDs in startup order. Fresh settings use an empty list.
- Installed package manifests declare the legacy flat JSON fields to import when their nested settings object is absent or null. The Heroes aliases are `HeroMode`, `HeroesMax` and `HeroSwitchReloadsMap`. The Cazacabezas aliases are `HeadHunters`, `HeadHuntWindowMinutes` and `HeadHuntRestMinutes`. Editing a package setting in the desktop UI saves its values in the package section. To migrate an installation that used embedded Heroes, stage a package built for server API version 2 and add `"heroes"` to `ServerMods`. The imported `Heroes.Enabled` value still gates a listed Heroes package. `Cazacabezas.Enabled` remains a gameplay toggle inside the loaded hunt package.
- The client projects target `net6.0` and reference MelonLoader and game IL2CPP assemblies. The built client DLLs stay under `Cazacabezas/` and `Heroes/`. Each server package's `StageServerMod` build target stages its manifest, server assembly and client DLL together.
- `Jondo.Unity.Core/Paths.cs` (`ModsDirectory`) resolves `<root>/mods`. `ServerSettings.ClientMods` defaults to `JondoFix.dll`. A loaded package supplies its declared DLL even when the order omits it. An existing explicit `ClientMods` entry can still serve the same-named loose DLL from `mods/` when its package is absent. `Jondo.Unity.Server/Network/ClientModCatalog.cs` (`ListAvailable`, `CreateManifest`, `OpenDownload`) discovers DLLs, emits configured available entries in order with name, byte size and SHA-256, skips missing files, excludes links, rejects invalid/duplicate names and enforces contract limits. `Jondo.Unity.Server/Network/ControlApi.cs` (`Mods`) and `Jondo.Unity.Server/Network/HaapiServer.cs` (`ServeClientModAsync`) serve unauthenticated `GET /api/mods` and `GET /api/mods/{name}`. Downloads require an exact configured name. `Jondo.Unity.Contract/ClientModManifest.cs` caps manifests at 128 entries and each DLL at 128 MiB. An empty order serves an empty manifest when no loaded packages supply client assets.
- The launcher and MCP use `Jondo.Unity.Contract/ClientModDeployment.cs` to verify the server's manifest and cache its DLLs by SHA-256. The launcher stores explicit approvals per host. MCP trusts the manifest of the local server that accepted its test key. `JondoModLoader/ServerModsPlugin.cs` verifies the cached files and loads them in order. When an older compatible server returns 404 for the manifest, these clients discover loose DLLs from `mods/`.
- `Cazacabezas/CazacabezasMod.cs` (`CazacabezasMod`, F7) and `Heroes/HeroesMod.cs` (`HeroesMod`, Ctrl+Tab and Tab switching) POST JSON to `http://127.0.0.1:8888/api/`. Both read `JONDO_PLAYER_TOKEN` with `JONDO_CONTROL_TOKEN` as fallback. Active gameplay routes below go through `ControlApi.Responder` → `ConSesion`, which resolves the body’s `token` via `Jondo.Unity.Server/Network/ClientLaunchRegistry.cs` (`ResolveToken`, registry then DB `LauncherToken` and `GameToken`). Invalid tokens receive `401 sesion`. These gameplay routes accept the same requests regardless of HTTP verb. `ControlApi.InTheWorld` finds the authenticated account’s in-world character with an active selection.

## Cazacabezas

- **Settings** — `HuntSettings.Enabled=true`, `HuntWindowMinutes=15`, `RestMinutes=30`. Legacy aliases `HeadHunters`, `HeadHuntWindowMinutes`, `HeadHuntRestMinutes`. `HeadHunts.Window` and `Rest` enforce minima of 1 and 0 minutes respectively.
- **Client work** — `CazacabezasMod.OnUpdate` polls status every 2 seconds with its window or an active hunt, otherwise every 6 seconds, with error backoff. It adds an alignment-tab button and displays the hunt tracker. `OnInitializeMelon` leaves the UI idle without a player token.
- **Routes** — `Cazacabezas.Server/CazacabezasMod.cs` registers and owns `/api/headhunts/{status,board,take,abandon}`. The host authenticates each request before invoking the plugin. All four routes require the authenticated account's in-world character or return `409 world`. `take` reads the target character ID from `target`. With the package absent from `ServerMods`, all four paths return `404 ruta`.
- **Package and host.** `Cazacabezas.Server/HeadHunts.cs` owns eligibility, targets, holds, streaks, orders, currency, and payouts. `HuntResponses.cs` projects the status and board JSON, and `HuntTexts.cs` contains localized notices. The module registers generic hooks for activity admission, currency, settlement and simulated targets. The host supplies world lookups, character lifecycle, ordinary alignment and PvP changes, and other game services through the shared contract.
- **Development setup** — Run `dotnet build Cazacabezas.Server/Cazacabezas.Server.csproj -p:StageServerMod=true` to stage the manifest, server DLL and existing client DLL in `mods/cazacabezas/`. Pass `-p:CazacabezasPackageDirectory=<path>` to stage elsewhere. Add `"cazacabezas"` to `ServerMods` in `config/server_settings.json` and restart. `Cazacabezas.Enabled` controls hunt rules. The manifest’s `clientAsset` supplies `Cazacabezas.dll` to the catalog. An entry in `ClientMods` places it in the configured order, and the catalog appends it when that entry is omitted. The test project stages the same three files in its own fixture.
- **Legacy client DLL** — The package asset takes precedence over an identically named standalone DLL in `mods/`. After package removal, the server can still advertise and serve a standalone `mods/Cazacabezas.dll` if an existing explicit `ClientMods` order includes it. For a full uninstall, remove the package ID from `ServerMods`, move the package out of `mods/`, and remove any legacy loose DLL from the server's `mods/` and from the client's MelonLoader `Mods/` directory. Restart to apply these changes.
- **Storage and clock** — The package lazily loads/creates the existing tables through the host's SQLite connection to `DatabaseManager.WorldConnectionString` (`bases/world.db` by default). `HeadHunters` is keyed by `CharacterId` with `Chapeuvepes`, `Kills`, `Survivals`, `Failures`, `Streak`, `LockedUntilMs`. `HeadHuntOrders` is keyed by `Hunter` with `Target`, `TakenMs`, `OpenUntilMs`, `RestUntilMs`. Both participants are character IDs and timestamps use Unix milliseconds. Reload clears open state and keeps the later of the saved open/rest deadlines. `CazacabezasMod` owns the non-overlapping five-second tick. It starts when the host signals that gameplay services are online and waits for any running tick on shutdown. An unloaded package schedules no work. The tick honors `Cazacabezas.Enabled`. With the package loaded, that setting keeps its existing behavior for routes, persisted balances, and character cleanup.

| Gameplay operation | Hook and ordering |
| --- | --- |
| Alignment and deletion | `AlignmentHandler.PvpAsync` checks whether a hunt blocks folding wings before setting PvP. `SwitchAsync` invalidates orders after the alignment change and before refreshing the client. `CharacterDeletionHandler.DeleteAsync` removes orders and the hunter record after successful deletion. |
| Fight admission | `ChallengeDuelHandler.OfferAsync` and `AnswerAsync` ask the package about the pair of characters before proceeding. `FightHandler.AttackAsync` and the guarded branch of `FightJoin.JoinRequestAsync` still check the hunt hold before admission. `MonsterAggression.Find` skips automatic aggression against an actively hunted character. |
| Other admission | The generic activity-admission hook runs in `DungeonHandler.AtTheDoorAsync`, `TryResumeAsync`, and `EnterByItemAsync`, `ExpeditionHandler.ReplyAsync`, `HouseHandler.GoToHouseAsync` and `EnterAsync`, `MerkasakoHandler.EnterFromOutsideAsync` and `AnswerInvitationAsync`, and `KoliseoHandler.EnrolPartyAsync` and `AnswerOfferAsync`. Existing jail checks still precede hunt holds where both apply. |
| Fight settlement | `FightHandler.EndFightAsync` calls `AlignmentWar.AnnounceAsync`. After the ordinary cities-war honour announcements, the hook passes the winning and losing characters and their fight levels to the package. The package updates streaks and settles open orders, then sends outcome notices. |
| Shop payment | `NpcHandler.SeguirLaConversacionAsync` debits chapeuvepés before giving the item and refunds the debit if delivery fails. With the package unloaded, these currency purchases follow the existing payment-refused dialogue. |
| Simulated hunters | `SimulatedPlayer.TakeAnOrderAsync` asks the package to choose an eligible online order using the bot's random selection. `HuntAsync` asks for the active target, then keeps movement and fight initiation in the host. |

Without the package loaded, hunt checks allow ordinary admissions and wing changes, and hunt notifications and settlement are skipped.

## Heroes

- **Settings** — `Enabled=true`, `MaxHeroes=7`, `ReloadMapOnSwitch=false`. Legacy aliases `HeroMode`, `HeroesMax`, `HeroSwitchReloadsMap`. The package clamps the follower count to 1–7.
- **Client work** — `HeroesMod.OnInitializeMelon` installs client UI hooks when a token is available. `OnUpdate` polls the list every 3 seconds with a window or group panel, otherwise every 8 seconds, with auth-error backoff. The group panel uses server state for character controls, life and portraits.
- **Routes** — `Heroes.Server/HeroesMod.cs` and `HeroesMod.Characters.cs` own `/api/heroes/{list,connect,disconnect,portrait,quests,autopass,loot,sheet,switch,create,name,preview}` and their validation and responses. The host authenticates before dispatch. The package checks group ownership for sheets and account ownership for portraits. Name and preview require a token. Creation uses the host's ordinary character creation service and then tries to connect the new character. The host supplies character data, creation, appearance rendering and the combat sheet through typed operations.
- **Activation and staging** — Run `dotnet build Heroes.Server/Heroes.Server.csproj -p:StageServerMod=true` to copy the manifest and both DLLs into `mods/heroes/`. Pass `-p:HeroesPackageDirectory=<path>` to stage elsewhere. Add `"heroes"` to `ServerMods` and restart. Use a Heroes package built against this host's contract. `Heroes.Enabled=false` skips it even when listed. Absent and skipped packages return `404 ruta` on their gameplay routes. Shutdown unregisters their routes and hooks.
- **Client asset** — The Heroes manifest declares `Heroes.dll`. The package copy takes precedence over a standalone copy with that name. The catalog appends the asset if `ClientMods` omits it. For a full uninstall, remove the package ID and remove any legacy loose `Heroes.dll` from the server's `mods/` and the client's MelonLoader `Mods/`. Restart to apply the changes.
- **Storage and lifetime** — `Heroes.Server/HeroGroups.cs` owns active membership, group order, auto-pass, loot routing, quest-sharing preferences, and the account-keyed switch and regroup records. The package drains active groups on shutdown. The host still persists character state through `DatabaseManager.SaveCurrentCharacter` using the existing character IDs and tables. Dungeon keyring and progress, hub wins, and zaap discoveries retain their existing identifiers and storage. The client stores `Heroes.PanelX` and `Heroes.PanelY` in `UserData/MelonPreferences.cfg`.

| Gameplay operation | Package ownership and remaining host dependencies |
| --- | --- |
| Session ownership and teardown | `HeroGroups` validates the master and checks ownership, online status, duplicate activation, fight state and capacity. The host validates the account and loads a socketless session through the companion service. Normal character selection still validates the account and server in the host and releases an active hero before loading it for the client. `SessionTeardown.LeaveAsync` tells the package the master left. The package drops the active group and keeps a three-minute regroup record. The host removes map and party presence, saves the character and notifies guilds and alliances through typed calls from the package. `SessionRegistry.IsOnline` reads the package's active membership. |
| Switching | The package serializes immediate switching on the real session, holds follower party updates and transfers group ordering and loot routing. The host saves and selects on the existing session and sends normal character, map, journal and shortcut state. `GameEntryService` reads pending switches for the two-minute deferred selection, filters the requested character through the host's account/server list, and notifies the package after entry for regroup. |
| Party and trade | The package decides which followers leave or join the master's party and suppresses follower churn during a switch. `PartyHandler` still applies changes to the authoritative `Parties` roster and sends the party frames. The package owns invitation redirection, waiting for the master to answer a pending invitation, follower-only party exceptions, kick and promotion restrictions, succession, and follower-only dissolution. `TradeHandler` asks the package to redirect outside requests or supply the auto-accepting follower at the existing point before the host checks map and busy state. The host commits and sends ordinary party and exchange operations. |
| Movement and presence | `WorldMoveHandler.ConfirmMovementAsync` and `SessionRegistry.AnunciarMudanzaAsync` notify the package after normal movement and map changes. The package positions and moves followers and asks the host to draw, walk or remove actors. Zaap discovery, map achievement updates, geometry, and packet delivery use host services. The package selects which followers are visible. `MapInformationMessages.Actors` builds their actors with ordinary character look services. |
| Dungeon admission and payment | The door, matchmaking and teleport-item paths call the generic companion admission hook before committing entry. The package selects out-of-fight followers and assigns eligible keyrings or spare keys after the host reserves the master's own payment. A follower's quest can make a physical key preferable. The host validates the plan, commits all group debits together, then moves the admitted roster and records progress. The door still shares quest credit with keyed followers. `TryResumeAsync` asks the package whether the selected followers have the same saved room after the ordinary resume, jail, hunt and level checks, before dialogue close and teleport. |
| Fight creation, joining and turns | `FightHandler.InitiateFightFromMobCollision` asks `IFightParticipantPolicy` for opening participants before setting fight state or adding fighters. `HeroGroups.Fights.cs` selects active followers in group order, skips those already fighting, and respects the host's side cap, including solo quests and anomalies. The host builds each follower from their own sheet and spell choices, removes their map actor, and commits their ready fighter and controller before adding the companion and sizing the enemy group. Manual and party auto-joins continue through `FightJoin` with the existing placement, party, map and capacity checks. Heroes recruitment runs on the PvM opening path. Ordinary fight rejoin uses the saved fight roster and the host's character-selection checks. The package orders the controlled spell bar and selects auto-pass at the existing point after start-of-turn events. The host still checks who can act, directs turns and summons to the controller, and handles a missing or disconnected controller. |
| Rewards and fight exit | `PlanRewards` resolves socketless participants through the package and rolls each winner's rewards using their own session. Outside Dreams, the package selects loot transfers before the host publishes or applies the plan. The host moves only shared items and their affixes, keeping XP, kamas and personal drops with their earners. `EndFightAsync` claims settlement once, completes ordinary clients first, then settles eligible socketless fighters whose session still belongs to that fight. The host records achievement, challenge and dungeon progress, clears fight state and saves. After the host returns clients to the map, the package restores follower presence. Departed groups still follow the existing detach and regroup path. |
| Quest sharing and turn-ins | The package selects eligible followers, applies the shared action, tracks lag, localizes lag notices, and pools spare items before the master commits the step. It owns the NPC reply restrictions (purchases, item removal, marks, dungeon doors, return-home, phoenix and mutation), and checks NPC presence and reply availability in the follower's context. `NpcHandler`, `Quests`, `ReadingItemHandler`, `DungeonHandler` and `QuestWatcher` supply ordinary quest actions and progress checks at their original call sites. Fight objectives still share after crediting the master and before clearing follower fight IDs. The host remains responsible for quest rules, inventory ownership and transfer, achievement delivery and protocol output. |

The server offers gameplay capabilities to any package through `GetService<T>()` and `RegisterHook<T>()`. The base `.allycontrol` command and keyed `/api/test` harness exercise controlled allies and companion selection.

## Runtime loading and release packaging

- The host targets `net10.0` with `SelfContained=false`, `PublishSingleFile=true` and `IncludeNativeLibrariesForSelfExtract=true`. Its RID defaults to `NETCoreSdkRuntimeIdentifier`. Checked-in publish paths specify neither trimming nor Native AOT. The framework-dependent apphost requires .NET 10.
- Enabled packages initialize in `ServerMods` order, receive the gameplay-start signal after services come online, and shut down in reverse order. Their routes and declared client assets come from the same loader instance. The default host can start with no `mods/` directory.
- Deploy optional packages alongside the release or mount the `mods/` directory in Docker. List each selected package ID in `ServerMods`. A missing package ID is logged and skipped.

## Package matrix from the original migration

The Windows .NET 10 framework-dependent build ran through the normal `--headless` entry point. Each run started with its own `ServerMods` list, used the same ordinary character through the connection and game sockets, disconnected, re-entered and shut down cleanly. The original test also had both standalone client DLLs in `mods/` with the default client order. The catalog and downloads matched the enabled packages and each downloaded DLL matched its declared size and SHA-256.

| `ServerMods` | Authenticated gameplay routes | Package client assets |
| --- | --- | --- |
| `[]` | None | None |
| `["cazacabezas"]` | `/api/headhunts/*` | `Cazacabezas.dll` |
| `["heroes"]` | `/api/heroes/*` | `Heroes.dll` |
| `["cazacabezas", "heroes"]` | Both route sets | Both DLLs in startup order |

All 16 mod paths returned `401 sesion` with an invalid token when their package was loaded and `404 ruta` when it was absent. Authenticated Heroes list and Cazacabezas status requests worked against the same live game session in the combined run. The in-process package matrix also checked that shutdown removed both route sets. The standalone host build, evaluated MSBuild project items and assembly metadata were checked for dependencies and embedded resources from either server plugin.

## Cazacabezas package verification

The earlier Cazacabezas settlement smoke test used the built server's normal `--headless` entry point and a fresh installation. Direct checks against the running process covered the activation log, catalog name/size/SHA-256, download bytes, all four authenticated hunt routes, restart persistence, and removal. The package served the existing 42,496-byte DLL with SHA-256 `75438a80015c75280d4766a979e7dfc8f85bb0f8d29457bbc0a18906f3291801`.

The existing `/api/test` harness supplied two headless clients for the live gameplay checks. Both connected through the game sockets. The hunt clock opened the order, the hunter sent the ordinary aggression frame, and the prey surrendered through its own socket. The production fight settlement removed the order and paid four chapeuvepés. A second open order survived a graceful process restart as a resting order, alongside the balance and kill count. With the package removed, all four routes returned `404 ruta` and an expired order stayed untouched through two five-second tick intervals. The configured standalone DLL still served in that run. Moving it out of `mods/` and restarting with the same `ClientMods` list produced an empty catalog and a `404` download.

### Repeat the Windows smoke test

Run in PowerShell 7 from the repo root with the .NET 10 SDK and Python 3. Use an unused set of server ports (8888, 15881, 5555, 5556 and 6337) and a Windows session that can open loopback `HttpListener` endpoints. The setup uses the shipped world archive and a fresh auth database.

```powershell
$stage = (New-Item -ItemType Directory (Join-Path (Resolve-Path .) "host-artifacts/cazacabezas-$([guid]::NewGuid().ToString('N'))") -Force).FullName
dotnet build Jondo.Unity.Server/Jondo.Unity.Server.csproj -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Server build failed' }
dotnet build Cazacabezas.Server/Cazacabezas.Server.csproj -m:1 -nr:false -p:StageServerMod=true "-p:CazacabezasPackageDirectory=$stage/mods/cazacabezas"
if ($LASTEXITCODE) { throw 'Package build failed' }
Copy-Item .jondo-root $stage
Copy-Item datos,content -Destination $stage -Recurse
New-Item -ItemType Directory -Path "$stage/config","$stage/mods" | Out-Null
$server = (Resolve-Path 'Jondo.Unity.Server/bin/Debug/net10.0/win-x64/Jondo Server.dll').Path
$env:JONDO_EMULATOR_ROOT = $stage
$env:JONDO_PUBLIC_BIND = '0'
$env:JONDO_TEST_KEY = [guid]::NewGuid().ToString('N')
$dll = 'Cazacabezas.dll'

function Start-Run($label, $packages, $clientMods) {
    @{ ServerMods = $packages; ClientMods = $clientMods; Cazacabezas = @{ Enabled = $true } } |
        ConvertTo-Json -Depth 4 | Set-Content "$stage/config/server_settings.json"
    $script:serverLog = "$stage/$label.log"
    $script:process = Start-Process dotnet -ArgumentList ('"{0}" --headless' -f $server) -WorkingDirectory $stage `
        -RedirectStandardOutput $serverLog -RedirectStandardError "$stage/$label.err" -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddMinutes(2)
    while (!(Select-String -Quiet -SimpleMatch 'ALL EMULATION SERVICES ONLINE AND READY!' $serverLog)) {
        if ($process.HasExited -or (Get-Date) -gt $deadline) { throw "Server startup failed. See $serverLog" }
        Start-Sleep -Milliseconds 250
    }
    Select-String -SimpleMatch '[Server mods] cazacabezas (cazacabezas) initialized' $serverLog
}
function Stop-Run {
    $signal = [Threading.EventWaitHandle]::OpenExisting('Local\JondoEmuServidor.Stop')
    try { [void]$signal.Set() } finally { $signal.Dispose() }
    if (!$process.WaitForExit(20000) -or $process.ExitCode) { throw "Server shutdown failed. See $serverLog" }
}
function Post($route, $body, $expected = 200) {
    $reply = Invoke-RestMethod "http://127.0.0.1:8888/api/$route" -Method Post -ContentType 'application/json' `
        -Body (ConvertTo-Json -InputObject $body -Depth 6 -Compress) -SkipHttpErrorCheck -StatusCodeVariable code
    if ($code -ne $expected) { throw "$route returned $code. Expected $expected" }
    $reply
}
function Probe($route, $slot = 0, $body = @{}) {
    Post "test/$route" ($body + @{ key = $env:JONDO_TEST_KEY; slot = $slot })
}
function Hunt($route, $body = @{}, $expected = 200) {
    Post "headhunts/$route" ($body + @{ token = $token }) $expected
}
function Check-Catalog {
    $catalog = (Invoke-RestMethod 'http://127.0.0.1:8888/api/mods').mods
    $download = "$stage/download.dll"
    Invoke-WebRequest "http://127.0.0.1:8888/api/mods/$dll" -OutFile $download
    $hash = (Get-FileHash "Cazacabezas/$dll").Hash.ToLowerInvariant()
    if ($catalog.Count -ne 1 -or $catalog[0].name -cne $dll -or $catalog[0].sha256 -cne $hash -or
        $catalog[0].size -ne (Get-Item "Cazacabezas/$dll").Length -or (Get-FileHash $download).Hash.ToLowerInvariant() -cne $hash) {
        throw 'Catalog or download mismatch'
    }
}
function Wait-HuntOpen {
    for ($attempt = 0; $attempt -lt 25; $attempt++) {
        $order = (Hunt status).order
        if ($order.open) { return $order }
        Start-Sleep -Seconds 1
    }
    throw 'Hunt did not open'
}
```

Start with the package alone, walk through the authenticated operations, and have the prey surrender. The status after surrender should show `kills: 1`, `chapeuvepes` equal to the quoted bounty, and `order: null`.

```powershell
Start-Run installed @('cazacabezas') @($dll)
Check-Catalog
$hunter = (Probe create 0 @{ name = 'Cazador-Smoke'; breed = 8; level = 100 }).id
$prey = (Probe create 1 @{ name = 'Presa-Smoke'; breed = 8; level = 110 }).id
$token = (Probe token).token
$routes = 'status','board','take','abandon'
foreach ($route in $routes) {
    Post "headhunts/$route" @{ token = 'invalid' } 401
    Hunt $route @{} 409
}
Probe headless
Probe align 0 @{ side = 3; pvp = 1 }
Probe headless 1
Probe align 1 @{ side = 4; pvp = 1 }
Probe goto 0 @{ map = 101715467; cell = 300 }
Start-Sleep -Seconds 2
Probe goto 1 @{ map = 101715467; cell = 314 }
Start-Sleep -Seconds 2
Hunt status
(Hunt board).prey | Where-Object id -eq $prey
Hunt take @{ target = $prey }
Hunt take @{ target = $prey } 409
$bounty = (Wait-HuntOpen).bounty
Probe 'headless/send' 0 @{ op = 'hph'; fields = @{ '1' = $prey } } | Out-Null
$deadline = (Get-Date).AddSeconds(25)
while ((Probe fight).state -ne 'Ongoing') {
    if ((Get-Date) -gt $deadline) { throw 'Aggression did not start' }
    Start-Sleep -Milliseconds 250
}
Probe 'headless/send' 1 @{ op = 'kme' } | Out-Null
$deadline = (Get-Date).AddSeconds(25)
do {
    $settled = Hunt status
    if ((Get-Date) -gt $deadline) { throw 'Hunt did not settle' }
    Start-Sleep -Milliseconds 250
} until ($settled.kills -eq 1 -and $null -eq $settled.order)
if ($settled.chapeuvepes -ne $bounty) { throw 'Bounty mismatch' }
Hunt take @{ target = $prey }
Wait-HuntOpen
Stop-Run
```

On restart, check that the same token resolves from the auth database. After reconnecting the hunter, expect the saved kill and balance, the same target, `open: false`, and a positive `restSeconds`. Use the other slot to check abandoning an order and its subsequent `409 locked` response.

```powershell
Start-Run restarted @('cazacabezas') @()
Check-Catalog
Hunt status @{} 409
Probe headless
$restored = Hunt status
if ($restored.kills -ne 1 -or $restored.chapeuvepes -ne $bounty -or $restored.order.target -ne $prey -or
    $restored.order.open -or $restored.order.restSeconds -le 0) { throw 'Persisted hunt state mismatch' }
Probe headless 1
$hunterToken = $token
$token = (Probe token 1).token
Hunt take @{ target = $hunter }
Hunt abandon
Hunt status
Hunt take @{ target = $hunter } 409
$token = $hunterToken
Stop-Run
```

Remove the package and age the test order so an active hunt clock would delete it even with both players offline. With the legacy DLL present, expect the same download bytes, `404 ruta` on the gameplay routes and identical SQLite rows before and after waiting. After moving the legacy DLL out of `mods/`, expect an empty manifest and a `404` download as well.

```powershell
Move-Item "$stage/mods/cazacabezas" "$stage/uninstalled-cazacabezas"
Copy-Item "Cazacabezas/$dll" "$stage/mods/$dll"
$db = "$stage/bases/world.db"
@'
import sqlite3, sys, time
with sqlite3.connect(sys.argv[1]) as db:
    db.execute('UPDATE HeadHuntOrders SET TakenMs=?, OpenUntilMs=0, RestUntilMs=0 WHERE Hunter=?',
               (int((time.time()-4*86400)*1000), int(sys.argv[2])))
'@ | python - $db $hunter
function Read-Order {
    python -c 'import sqlite3,sys; print(sqlite3.connect(sys.argv[1]).execute("SELECT * FROM HeadHuntOrders WHERE Hunter=?",(int(sys.argv[2]),)).fetchone())' $db $hunter
}
$due = Read-Order
if ($due -eq 'None') { throw 'Missing test order' }
Start-Run legacy-only @() @($dll)
Check-Catalog
foreach ($route in $routes) { Hunt $route @{} 404 }
Start-Sleep -Seconds 11
if ((Read-Order) -ne $due) { throw 'Hunt clock changed the saved order' }
Stop-Run
Move-Item "$stage/mods/$dll" "$stage/legacy-$dll"
Start-Run removed @() @($dll)
(Invoke-RestMethod 'http://127.0.0.1:8888/api/mods') | ConvertTo-Json
(Invoke-WebRequest "http://127.0.0.1:8888/api/mods/$dll" -SkipHttpErrorCheck).StatusCode
foreach ($route in $routes) { Hunt $route @{} 404 }
Stop-Run
```

All six `HeadHuntTests` also passed in the in-process harness. Those tests use fixture sessions and the cities-war callback to cover holds, timeout, win and loss payouts, currency trades, legacy SQLite rows, and disposal. To repeat, use a scratch data root containing `.jondo-root`, `datos/`, `content/`, and `bases/world.db` extracted from `datos/world.zip`. Point `JONDO_EMULATOR_ROOT` at that root and reuse the compiled test assembly.

```powershell
dotnet test Cazacabezas.Server/Tests/Cazacabezas.Server.Tests.csproj --filter FullyQualifiedName~HeadHuntTests
```

**Remaining manual check** — Launch a supported Dofus client through the launcher against the packaged installation. Confirm MelonLoader loads the downloaded DLL, F7 and the alignment-tab button open the hunt panel, and the tracker follows a live order through settlement. Repeat against the fully removed installation with any old client-local `Mods/Cazacabezas.dll` moved aside. This smoke test used a framework-dependent build. Published single-file and Docker smoke tests were outside the scope of this historical run.

## Test entry points

Run the base suites and the installed modules' own test projects from the repository root with
the .NET 10 SDK. Gameplay fixtures use a test data root with the world database from
`datos/world.zip`.

```sh
dotnet test Jondo.Unity.Tests/Jondo.Unity.Tests.csproj
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
dotnet test Cazacabezas.Server/Tests/Cazacabezas.Server.Tests.csproj
dotnet test Heroes.Server/Tests/Heroes.Server.Tests.csproj
```

The core suites cover the loader, generic hooks, client asset ordering and verification, and
ordinary gameplay. The optional test projects cover their package's routes and gameplay.
The server's publish gate runs the core suites. CI discovers optional test projects in each
module's `Tests/` directory.

## Unverified behavior

Live MelonLoader/IL2CPP behavior across client versions and hosts, recovery from a live server crash, and recovery after a partial Heroes key debit or interrupted handover are unverified end to end.

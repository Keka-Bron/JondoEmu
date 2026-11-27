# Writing server mods

Server mods are .NET 10 class libraries loaded by `Jondo.Unity.Server` from packages in `mods/`. A mod calls host services to read or change game state, registers gameplay hooks for callbacks from the server, and can expose authenticated control routes to a client. The shared interfaces live in [`Jondo.Unity.Contract`](../Jondo.Unity.Contract/IServerMod.cs) under the `Jondo.Unity.ServerMods` namespace.

## Create a server-only mod

This example logs alignment changes and exposes a read-only route that reports the authenticated account's in-world character. Create `AlignmentAudit/AlignmentAudit.csproj` at the repository root using the .NET 10 SDK.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Jondo.Unity.Contract/Jondo.Unity.Contract.csproj" Private="false" />
  </ItemGroup>
</Project>
```

Create `AlignmentAudit/AlignmentAuditMod.cs`. The package exports one public, concrete `IServerMod` with a public parameterless constructor, which C# supplies here.

```csharp
using System.Text.Json;
using Jondo.Unity.ServerMods;

namespace AlignmentAudit;

public sealed class AlignmentAuditMod : IServerMod
{
    public void Initialize(IServerModHost host)
    {
        var world = host.GetService<IAlignmentWorld>();
        host.RegisterHook<IPvpEvents>(new AlignmentEvents(host.Log));

        host.RegisterAuthenticatedControlRoute("/api/alignment-audit/status", request =>
        {
            if (request.Method != "POST")
                return new(405, """{"error":"method"}""");

            var character = world.FindSelectedCharacter(request.AccountId);
            if (character is null)
                return new(409, """{"error":"world"}""");

            return new(200, JsonSerializer.Serialize(new
            {
                characterId = character.Id,
                name = character.Name
            }));
        });
    }

    private sealed class AlignmentEvents(Action<string> log) : IPvpEvents
    {
        public void AlignmentChanged(long characterId) =>
            log($"Alignment changed for character {characterId}.");

        public void CharacterDeleted(long characterId) { }

        public Task AggressionEndedAsync(IReadOnlyList<PvpCombatant> winners,
            IReadOnlyList<PvpCombatant> losers) => Task.CompletedTask;
    }
}
```

Create `AlignmentAudit/manifest.json`. The API version must match `ServerModApi.Version` in the server you deploy to.

```json
{
  "id": "example.alignment-audit",
  "version": "1.0.0",
  "serverApiVersion": 2,
  "serverAssembly": "AlignmentAudit.dll"
}
```

Build and stage the package. These commands run in PowerShell from the repository root, using the checkout as the emulator root.

```powershell
dotnet build AlignmentAudit/AlignmentAudit.csproj -c Release
New-Item -ItemType Directory -Force mods/example.alignment-audit | Out-Null
Copy-Item AlignmentAudit/manifest.json, AlignmentAudit/bin/Release/net10.0/AlignmentAudit.dll `
    -Destination mods/example.alignment-audit
```

Add `"example.alignment-audit"` to the existing `ServerMods` array in `config/server_settings.json`, or select the installed package in the desktop server settings. Restart the server and look for the `[Server mods]` initialization message. If you run a separate server installation, copy the package into its `mods/` and edit that installation's settings.

You can also install this single-assembly example by copying `AlignmentAudit.dll` directly into `mods/` and using `"AlignmentAudit.dll"` as the `ServerMods` entry. Choose one installation form for the example.

Enter the world and change your character's alignment through the normal game flow. The server log should show `Alignment changed for character ...`. To exercise the route on the default local control port, supply a valid player token in `$playerToken` for the account you signed in with.

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8888/api/alignment-audit/status `
    -ContentType 'application/json' -Body (@{ token = $playerToken } | ConvertTo-Json -Compress)
```

Expect `characterId` and `name` for the account's selected character. A missing or invalid token returns `401`. A valid token without an in-world character returns `409`. To check the base-server path again, remove the package ID from `ServerMods` and restart. The mod route then returns `404`, and alignment changes no longer generate the mod's log message.

## Connect to the main server

In `Initialize`, get the host capabilities you need and register the hooks and routes your mod implements. [`ServerModLoader.Host`](../Jondo.Unity.Server/Mods/ServerModLoader.cs) maps service interfaces to server adapters. For example, `IAlignmentWorld` provides character presence and PvP operations, `ICharacterWorld` provides character and session operations, and `IWorldStorage` opens a world database connection that the caller disposes. See the `Services` map for the complete list. A request for an unregistered service throws. Keep feature rules and state in the mod and call host operations for game state changes.

Pass the contract interface explicitly to `RegisterHook<T>`, as in `RegisterHook<IPvpEvents>(...)`. The host dispatches by that exact interface. For alignment changes, [`AlignmentHandler`](../Jondo.Unity.Server/Handlers/AlignmentHandler.cs) applies the change, then calls [`PlayerPolicies.AlignmentChanged`](../Jondo.Unity.Server/Mods/PlayerPolicies.cs), which invokes each registered `IPvpEvents` handler. Registrations follow `ServerMods` order. Each gameplay call site defines how to combine them.

| Server dispatch | Example |
| --- | --- |
| A notification caller can iterate `ServerModHooks.All<T>()` and invoke each listener. | `PlayerPolicies` dispatches `IPvpEvents.AlignmentChanged` and `CharacterDeleted` this way. |
| A policy caller can stop when it has a decision. | `PlayerPolicies.BlocksActivityAsync` tries `IPlayerActivityPolicy` implementations until one returns `true`. With no veto, it returns `false`. |
| `ServerModHooks.Get<T>()` returns the first registered implementation or `null`. | `FightParticipants` reads the first `IFightParticipantPolicy`. The fight code handles the default path when the policy is absent. |

Control routes receive the account ID resolved from the JSON body's token. Use `request.AccountId` to look up the caller and check ownership or permissions for any resource identified by the request. Check `request.Method` when a route accepts specific verbs. Keep route paths under a package-specific `/api/` prefix to avoid collisions. See the [control route contract](server-mod-contract.md#authenticated-control-routes).

## Add an extension point to the server

When the contract has no hook for the gameplay operation you need, change the host and the shared contract together.

1. Define a public interface in `Jondo.Unity.Contract` for the specific event or policy. Use contract types for the data it needs. For a new host operation, give it a service interface, implement the adapter in `Jondo.Unity.Server/Mods/`, and map it in `ServerModLoader.Host.Services`.
2. Locate the server handler or manager that owns the operation. Invoke a policy at the decision point or notify listeners after the state change they are observing. Follow `AlignmentHandler` and `PlayerPolicies` for a notification example. Document what the handler does with zero implementations and how it combines several. Preserve ordinary gameplay when no mod registers the hook.
3. Register the implementation from `IServerMod.Initialize`. Build both the host and the package against the updated contract and verify the gameplay path with the package enabled and disabled. For a hook called by multiple packages, also check dispatch order and any short-circuit or first-provider behavior.

## Manage settings and lifetime

Declare package settings in the manifest and read the effective JSON from `host.Settings`. The [package settings reference](server-mod-contract.md#package-settings) shows the schema and how to gate package activation. Use `host.Log` for startup and runtime messages so the server includes the mod ID.

[`Program`](../Jondo.Unity.Server/Program.cs) loads packages and calls `Initialize` before opening listeners. Use `StartGameplay()` for timers or workers that need the listeners and gameplay services online. Hooks and routes are registered during initialization and can be invoked as soon as their server call sites become active. Callbacks and background work can overlap, so synchronize any shared mutable state they use.

Stop and drain background work in `Shutdown()` while the host services are still available. Register cleanup as you acquire resources so partial initialization can release them too. The loader removes registered hooks and routes automatically. For a timer example, see [`CazacabezasMod`](../Cazacabezas.Server/CazacabezasMod.cs). For a package that combines companion, party, movement, quest, dungeon and fight hooks, see [`HeroesMod`](../Heroes.Server/HeroesMod.cs).

The [server mod contract](server-mod-contract.md) covers private dependencies, package settings, optional client DLLs and loader errors.

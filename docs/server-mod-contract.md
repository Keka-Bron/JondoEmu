# Server mod contract

For a complete example and the server-side dispatch path, see [Writing server mods](writing-server-mods.md).

Place each package in its own directory under `<emulator root>/mods/`. The following manifest belongs at `mods/example/manifest.json`.

```json
{
  "id": "example.greeting",
  "version": "1.0.0",
  "serverApiVersion": 2,
  "serverAssembly": "server/Example.Greeting.dll",
  "clientAsset": "client/Example.Greeting.Client.dll"
}
```

`id` identifies the mod across package renames and upgrades. It starts with an ASCII letter or digit and can also contain dots, underscores and hyphens. ID comparison ignores case. An enabled ID must identify exactly one package. `version` is a nonempty release string. `serverApiVersion` is an integer and must equal `ServerModApi.Version`.

`serverAssembly` and `clientAsset` are package-relative file paths. Each declared file must exist. Use `/` for nested folders. The loader rejects absolute paths, `.` and `..` components, and links or junctions in package paths. Omit `clientAsset` or set it to `null` for a server-only mod.

An optional `dependencies` array lists required package IDs or standalone server DLL filenames. For example, `"dependencies": ["example.audit", "Example.Shared.dll"]` requires both entries to be enabled. The loader resolves IDs case-insensitively and starts requirements before their dependents. A missing, inactive, failed, or cyclic requirement causes its dependents to be skipped.

After a package completes startup, its declared client asset contributes one entry to `GET /api/mods` if its filename meets the client DLL naming rules. Download URLs use the exact filename from that manifest. The server looks up the approved file by name. Server assemblies, private dependencies, manifests, and other package paths are private.

Configured standalone DLLs in `mods/` keep their load order. A package DLL with the same name, ignoring case, takes the standalone entry's slot. Other package DLLs follow server startup order. If multiple packages declare the same filename, the first package to start wins. If the winning file becomes unavailable, its entry is omitted until it returns.

A standalone client DLL can still load when listed in `ClientMods` and no active package supplies that filename. When switching to a package, remove the old filename from `ClientMods` if the replacement uses a different filename or if disabling the package should stop serving the old DLL. For manifest-based launches, the launcher sets aside managed copies from the game's `Mods/` folder so the game loads the versions selected by the manifest.

The launcher asks the player to approve any client DLL that does not match the copy bundled with the launcher, showing the manifest fingerprint. An approved DLL runs with the player's operating system account and can read the sessions the launcher has saved. While any such DLL is part of a launch, the launcher hands no session token to the client, so the client features that call the server for the player (the JondoFix monster group tooltip and colour palette, the Heroes and Headhunters client mods, the administrator control token) stay off for that launch. First-party mods bundled with the launcher, including those shipped as `mods/<id>/` packages, need no approval.

## Install and enable server mods

When upgrading from an installation with a separate `server-mods/` folder, move its
packages and standalone DLLs into `mods/`. Keep the existing package IDs and DLL
filenames in `ServerMods`.

Drop a standalone server mod DLL directly into `<emulator root>/mods/`. Add its filename, including `.dll`, to `ServerMods` in `config/server_settings.json`. Use the manifest ID for a package directory. Both forms can appear in the same list.

```json
{
  "ServerMods": ["Example.Audit.dll", "example.greeting"]
}
```

The server visits entries in the listed order and starts each entry once, starting any enabled requirements first. An omitted, null, or empty list enables zero mods. Restart the desktop or headless server to apply settings, installs, or updates. Installing a compatible mod requires no server rebuild. By default, the server logs mod failures and continues with the mods that can start. If the mods directory cannot be read, it starts without server mods. Set `"FailOnServerModError": true` in `config/server_settings.json` to abort on directory I/O errors or selected mod startup exceptions. Missing packages and unavailable dependencies are logged and skipped.

A standalone DLL must implement the same `IServerMod` entry point as a package. Use a package to declare an API version, include private dependencies or a client DLL, or expose settings in the desktop editor. A standalone mod can read settings from a JSON object keyed by its filename.

## Package settings

An optional manifest `settings` object describes the package's settings for the server desktop editor.

```json
{
  "name": "Example greeting",
  "title": { "en": "Greetings", "es": "Saludos", "fr": "Salutations" },
  "activationSetting": "Enabled",
  "fields": [
    {
      "key": "Enabled",
      "default": true,
      "label": { "en": "Enable greetings", "es": "Activar saludos", "fr": "Activer les salutations" }
    }
  ]
}
```

Add this object as the manifest's `settings` property. The Mods page shows the localized `title` in the desktop's language and falls back to `name`, then to the package ID. The host reads the settings object under the manifest ID in `config/server_settings.json` and passes the effective values to `host.Settings`. Fields can declare localized labels and hints, numeric bounds and steps, units, and a `legacyKey` to read a former flat setting if the package section is absent. An optional `activationSetting` names a Boolean field. A false value skips the installed package even when its ID is listed. An entry in `ServerMods` is required for activation.

## Gameplay integration

Call `host.GetService<T>()` for authoritative game operations and `host.RegisterHook<T>(implementation)` to participate in a supported gameplay hook. The contract defines these interfaces by gameplay capability. Use the host to read and update world state, and keep package rules and state in the package assembly. Hook registrations are cleaned up with the package.

## Lifecycle

Target .NET 10 and reference `Jondo.Unity.Contract`. The server assembly must export exactly one concrete `Jondo.Unity.ServerMods.IServerMod` implementation with a public parameterless constructor. The server calls `Initialize(IServerModHost host)` once, after loading the database and world services and before opening network listeners or signaling readiness. `host.Log` labels messages with the package and mod ID.

The server calls the optional `StartGameplay()` in initialization order after opening listeners and starting gameplay services. Use it to begin timers and other background work that needs those services. If this call fails, the server shuts down the failed mod and any initialized dependents in reverse order before continuing. With `FailOnServerModError` enabled, it shuts down all initialized mods and aborts startup.

Use `host.RegisterCleanup(Action)` immediately after acquiring a resource so its cleanup runs even if a later step in initialization fails. On shutdown or startup rollback, the server calls `Shutdown()` on successfully initialized mods in reverse startup order, after the network listeners stop and while their host services are still available. Each mod's registered callbacks run in reverse registration order after its shutdown hook. If initialization throws, the server runs that mod's registered callbacks immediately. The server logs shutdown and callback errors and continues cleanup.

The server supplies its own `Jondo.Unity.Contract` assembly, including in single-file builds. Leave the contract DLL out of distributed packages. Each package has its own assembly load context. Put private managed DLLs beside the server assembly, or include the assembly's `.deps.json` and its referenced files within the package. Mods run with the server process's permissions.

The loader reads managed DLLs into memory so Windows can release the package files immediately. Loaded plugin assemblies have an empty `Assembly.Location`.

## Authenticated control routes

Call `host.RegisterAuthenticatedControlRoute(path, handler)` in `Initialize` to register an exact, case-sensitive path under `/api/`. Built-in routes take priority. Duplicate plugin registrations for the same path fail. The host removes a package's registrations on shutdown or failed initialization.

Before invoking the handler, the host resolves the JSON body's `token` using the same account registry as built-in control routes. Missing or invalid tokens return `401` with `{"error":"sesion"}`. The handler receives the resolved `AccountId`, HTTP method, and original body, and returns a status code and JSON body. The host applies no additional HTTP method restriction to registered paths.

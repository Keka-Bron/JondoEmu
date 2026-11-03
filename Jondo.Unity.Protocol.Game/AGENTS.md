# Stable Game protocol

This .NET 10 library owns JondoEmu's version-independent semantic Game API.
See `../docs/game-protocol.md` for architecture, messaging and generation.
Commands below run from the repository root.

## Ownership and conventions

- `Messages.cs` contains maintained C# records, abstract case types and enums.
  These are semantic models, not generated renamed protobuf classes.
- `IGameCodec.cs` owns the request/event/response bases, `GameFrame`, codec
  interface and semantic conversion exception.
- Keep this project independent of protobuf, obfuscated aliases/tags, database
  entities, socket framing and server implementation details.
- Concrete mapped records need one public semantic constructor and matching
  public properties. The generator validates names, types and nullability
  against the compiled stable assembly.
- Preserve meaningful absence with nullable members or explicit `None` case
  records. Do not invent a required case when a wire oneof is unset.
- Use `IReadOnlyList<T>` for repeated values, `IReadOnlyDictionary<TKey, TValue>`
  for maps and `byte[]` for bytes. Keep collections and their elements non-null.
- Stable enum ordinals do not establish wire values; mappings bind them explicitly.
- Keep `JsonDerivedType` declarations on case bases consistent with their concrete
  cases so mapped logging preserves both case names and derived payload fields.
- Requests and responses use signed `int` correlation IDs, including zero.
  Events have no UID. Codec input/output excludes the TCP length prefix.
- When changing a mapped member or case, update the binding in
  `Jondo.Unity.Protocol.Game.Wire/Mappings/` and relevant tests. Every stable
  constructor member and concrete case needs a binding or explicit construction
  policy; wire coverage need not be exhaustive.
- Use protocol versions, not extraction dates, to identify wire profiles.
  `3.6.10.10` is the canonical Game profile for both `3.6.10.10` and `3.6.10.11`
  clients, which use the identical Game protocol. Stable names stay version-independent.
- Keep documentation and maintenance workflows self-contained within JondoEmu.

## Verification

```sh
dotnet build Jondo.Unity.Protocol.Game/Jondo.Unity.Protocol.Game.csproj
dotnet test Jondo.Unity.Protocol.Game.Tests/Jondo.Unity.Protocol.Game.Tests.csproj
```

For changes affecting live messaging, movement or mapped logging, also run:

```sh
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
```

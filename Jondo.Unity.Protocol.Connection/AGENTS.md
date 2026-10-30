# Stable connection protocol

This .NET 10 library owns the hand-written, version-independent connection types.
See `../docs/connection-protocol.md` for the architecture and generation workflow.
Commands below run from the repository root.

## Ownership and conventions

- `Messages.cs` contains semantic records, abstract case types, and enums.
- `IConnectionCodec.cs` defines the payload codec boundary and semantic conversion error.
- Keep this project independent of protobuf, wire aliases/tags, database entities,
  transport framing, and server implementation details.
- Stable types are hand-written C#, not another generated `.proto` model. Express
  alternatives as abstract record types with concrete record cases.
- Concrete mapped records need exactly one public semantic constructor and public
  properties matching its parameters in name, type, and nullability. The generator
  validates these against the compiled assembly.
- Preserve absence explicitly with nullable members where it has semantic meaning.
  A required case must not invent a default when the wire oneof is unset.
- Enum numeric values are not wire values. Bind them explicitly in the versioned
  JSON mapping, including zero-valued enum branches of oneofs.
- When changing a mapped member or adding a case, update the corresponding mapping
  in `Jondo.Unity.Protocol.Connection.Wire/Mappings/` and its relevant tests.
- Use game versions such as `3.6.10.11` to identify wire schemas/mappings; stable
  type names remain version-independent.

## Verification

```sh
dotnet build Jondo.Unity.Protocol.Connection/Jondo.Unity.Protocol.Connection.csproj
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
```

For changes affecting authentication, server selection, or ticket handoff, also run:

```sh
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
```

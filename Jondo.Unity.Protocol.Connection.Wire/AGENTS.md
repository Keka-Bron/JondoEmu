# Connection wire types and conversions

This .NET 10 library owns the Dofus `3.6.10.11` connection schema, semantic mapping,
generated protobuf types, and generated stable↔wire conversions. The supported
`3.6.10.10` client shares this connection protocol. See
`../docs/connection-protocol.md` for worked mappings and standalone generation.
Commands below run from the repository root.

## Sources and generated output

- `Schemas/connection-3.6.10.11.proto` is the pinned wire structure. Preserve its
  obfuscated fields, tags, enum numbers, oneofs, optional presence, and repetition.
- `Mappings/connection-3.6.10.11.json` binds wire aliases to hand-written stable types.
  Keep tags, scalar types, and repetition in the schema rather than duplicating
  them as mapping metadata. Name schema/mapping files by game version.
- `ConnectionCodec.cs` exposes the stable `IConnectionCodec` boundary. Codec input
  and output are protobuf payloads without the transport's varint length prefix.
- `Grpc.Tools` generates complete wire types plus `connection.pb` from the schema
  alone. `GenerateConnectionBindings` separately validates the mapping and stable
  reference assembly, then generates `ConnectionBindings.g.cs`.
- Generated C# and descriptors live under the intermediate output directory
  (`obj/` by default). Change their sources and regenerate rather than editing or
  committing build output. The generator is a build-time dependency only.

## Conversion semantics

- Map all stable constructor members and concrete cases. Explicitly list omitted
  declared wire fields in `ignoredWireFields`; they retain protobuf defaults on output.
- Bind enum values explicitly. For example, wire status 0 is `Online`, regardless
  of the stable enum's numeric ordering.
- Preserve optional zero versus absence and present-empty message versus absence.
  An unset required oneof and unsupported enum values are conversion errors.
- Raw protobuf objects retain unknown fields. Stable projection is not a lossless
  wire round trip. Raw objects are available through `Messages` and the generated
  `ConnectionBindings.DecodeWire` helper.
- The mapping owns the root alias. Server transport should call the codec instead
  of selecting a version-specific generated root type.

## Generation and verification

```sh
dotnet build Jondo.Unity.Protocol.Connection.Wire/Jondo.Unity.Protocol.Connection.Wire.csproj
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
```

After restore, generate only wire types/descriptors independently of semantic bindings:

```sh
dotnet msbuild Jondo.Unity.Protocol.Connection.Wire/Jondo.Unity.Protocol.Connection.Wire.csproj -target:Protobuf_Compile
```

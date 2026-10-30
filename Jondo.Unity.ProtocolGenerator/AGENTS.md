# Protocol conversion generator

This .NET 10 build-time tool generates direct typed conversions from a protobuf
descriptor set, semantic JSON mapping, and compiled stable assembly. `Grpc.Tools`
owns wire-type generation. See `../docs/connection-protocol.md` for usage and worked
examples. Commands below run from the repository root.

## Implementation

- `Program.cs` accepts four positional arguments:
  `<descriptor.pb> <mapping.json> <stable.dll> <output.cs>`.
- `BindingGenerator.cs` reads protobuf descriptors for wire structure and Roslyn
  symbols for stable types. Keep semantic bindings separate from protobuf structure.
- Emit direct C# conversions and root helpers; conversion dispatch should not need
  reflection or runtime mapping-file loading in the server.
- Validate mappings before replacing the destination. Invalid input must fail with
  a useful diagnostic and leave an existing generated output intact.
- Check aliases, stable identities/members, constructor shape, type compatibility,
  repetition, optional presence, case coverage, and enum bindings. Reject duplicate
  keys, unknown policies, and unsupported shapes rather than guessing.
- Declared unmapped wire enum values/cases may remain in the raw layer; semantic
  conversion must reject them. Missing required messages/oneofs also fail conversion.
- Ignored wire fields are explicit and default on output. Never infer semantic
  enum values from their numeric order or invent a required case.
- The current supported shapes are documented in `docs/connection-protocol.md`.
  Extend support with executable synthetic-fixture tests when a real schema needs it.

## Verification

```sh
dotnet build Jondo.Unity.ProtocolGenerator/Jondo.Unity.ProtocolGenerator.csproj
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
dotnet build Jondo.Unity.Protocol.Connection.Wire/Jondo.Unity.Protocol.Connection.Wire.csproj
```

Generator tests belong in `Jondo.Unity.Protocol.Connection.Tests/GeneratorTests.cs`.
They compile and execute generated conversions against synthetic wire/stable types,
including rotated aliases/tags. Keep generic generator coverage independent of
production aliases; production compatibility belongs in `CodecTests.cs`.

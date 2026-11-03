# Game wire types and conversions

This .NET 10 library owns the complete Game wire schema, semantic bindings,
generated conversions and codec. The canonical protocol profile is `3.6.10.10`;
clients `3.6.10.10` and `3.6.10.11` use the identical Game protocol.
See `../docs/game-protocol.md`. Commands below run from the repository root.

## Sources and generation

- `Schemas/game-3.6.10.10.proto` owns wire structure. Preserve all declared fields,
  tags, scalar types, enum numbers, maps, oneofs, repetition and optional presence.
- `Mappings/game-3.6.10.10.json` owns semantic C# bindings, envelope roles and
  original wire aliases. Do not duplicate structural metadata in the mapping.
- `Grpc.Tools` independently generates complete protobuf types and a descriptor
  set. `GenerateGameBindings` then validates descriptors and the stable reference
  assembly through `Jondo.Unity.ProtocolGenerator` and emits direct conversions.
- Generated C# and descriptors belong under the intermediate output directory,
  including relocated `--artifacts-path` builds. Do not edit or commit build output.
  The generator remains a build-time dependency only.
- Keep the MSBuild `JondoVersion`, runtime `GameCodec.JondoVersion`, input names
  and documentation consistent. Use protocol versions rather than dates.
  Importing inputs does not automatically register another supported profile.

## Conversion and transport boundaries

- `GameCodec` consumes/returns protobuf envelope bytes without TCP length framing.
  Its `DecodeWire`, `ReadEnvelope` and `DecodeRawPayload` APIs expose raw inspection.
- Envelope roles and Any dispatch are mapping-driven. Preserve signed request/
  response UIDs, reject missing required envelopes/content and kind mismatches,
  and emit events without a UID.
- Unmapped wire fields and whole oneofs remain raw-only without ignore lists.
  Encoding leaves them at protobuf defaults/unset cases unless explicitly defaulted.
  Unmapped does not mean unused or decoy.
- Stable construction stays strict: validate every stable member and case, types,
  presence and enum bindings. Within mapped enums/oneofs, unsupported wire values
  or branches fail conversion; required messages/cases cannot silently default.
- `ignoredStableMembers`, `defaultOnAbsence` and `defaultOneofs` are explicit
  semantic construction policies. Preserve absent versus present-empty messages,
  optional zero values and selected zero-valued oneof branches.
- Standard protobuf raw decoding retains unknown fields. Stable projection is
  intentionally lossy and must not replace original bytes in raw diagnostics.
- Server handlers use typed `GameSession.SendAsync` and map-audience sends;
  keep routing, recipient selection and gameplay out of this library.

## Import maintenance

- `Import/import-game.py` accepts explicit `--schema`, `--mapping`, `--version`
  and optional `--destination` inputs. The mapping uses JondoEmu's C# binding
  format; no particular source-repository layout is required.
- The importer normalizes supported proto3 syntax, checks name collisions and
  binding references, and preserves original Any identities through `wireAliases`.
  Reject unsupported syntax/policies rather than silently discarding them.
- Stable C# models are maintained source and are never import outputs.
- Keep `Import/game-3.6.10.10.provenance.json` checksums consistent with installed
  inputs. Record project-relative paths and content hashes, not machine-specific
  paths or private repository references.
- Validate both inputs before replacing outputs. Publication is sequential;
  subsequent descriptor/Roslyn validation during build is authoritative.

## Verification

```sh
dotnet build Jondo.Unity.Protocol.Game.Wire/Jondo.Unity.Protocol.Game.Wire.csproj
dotnet test Jondo.Unity.Protocol.Game.Tests/Jondo.Unity.Protocol.Game.Tests.csproj
python3 Jondo.Unity.Protocol.Game.Wire/Import/test_import.py
```

After restore, the schema-only generation phase can run independently:

```sh
dotnet msbuild Jondo.Unity.Protocol.Game.Wire/Jondo.Unity.Protocol.Game.Wire.csproj -target:Protobuf_Compile
```

For shared generator changes, also run the Connection protocol suite; for live
messaging or logging changes, run the server suite:

```sh
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
```

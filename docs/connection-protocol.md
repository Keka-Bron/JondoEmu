# Stable connection protocol and generated wire types

The connection phase uses the 3.6.10.11 protocol, shared with the supported
3.6.10.10 client. Client discovery prefers 3.6.10.11. There is one connection codec.

## Ownership

```text
client .proto -> protoc -> complete obfuscated wire types
descriptor + semantic mapping + compiled stable types -> generated direct conversions
framed bytes <-> codec <-> stable messages <-> per-socket connection service
```

| Project / source | Owns |
|---|---|
| `Jondo.Unity.Protocol.Connection/Messages.cs` | Hand-written records, case types and semantic enums; no protobuf dependency |
| `Jondo.Unity.Protocol.Connection.Wire/Schemas/connection-3.6.10.11.proto` | Pinned wire structure |
| `Jondo.Unity.Protocol.Connection.Wire/Mappings/connection-3.6.10.11.json` | Field, case and enum semantics |
| `Jondo.Unity.ProtocolGenerator` | Descriptor/Roslyn validation and direct C# conversion generation |
| `Jondo.Unity.Server/Network/Connection/ConnectionService.cs` | Stable request handling and socket-owned authentication state |
| `ConnectionBackend.cs` | Database projection, launcher-token resolution, launch language and ticket issuance |
| `ConnectionTransport.cs` | Decode/handle/encode loop using the existing varint framing |

Generated C# and the descriptor set live under the wire project's intermediate
output directory (`obj/` by default). They are recreated on a clean build. The
generator is a build-time project reference and is not a server runtime dependency.

## Schema version and compatibility

The pinned schema describes the Dofus 3.6.10.11 connection protocol. Dotted type
declarations and references are flattened to their unique final segments
(`mig.mif.mia` becomes `mia`) for standard protobuf compilation. Field names,
tags, enum numbers, oneofs, optional presence and repetition are preserved.
Schema extraction is not part of this pipeline.

The compatibility test compares every message field and referenced enum against
Jondo's `datos/protocolo_conexion_3.6.10.11.proto`. That older extraction lacks
oneof groups and optional presence and includes case-discriminator enums.
Jondo's `.10` extraction also differs in unused authentication fields; it is not a
second runtime profile. Handshake tests pin the currently implemented reply bytes.

The former `Jondo.Unity.Server/Protocol/GameProtocol.proto` and manual connection
builders have been retired. `Network/ConnectionProtocol.cs` still builds game-node
messages; its name is historical.

## Generation

Run these commands from the repository root with the .NET 10 SDK.

```sh
dotnet build Jondo.Unity.Protocol.Connection.Wire/Jondo.Unity.Protocol.Connection.Wire.csproj
```

MSBuild runs two independent phases:

1. `Grpc.Tools` generates ordinary `Google.Protobuf` classes and a descriptor set
   from the `.proto` alone (`GrpcServices=None`).
2. `GenerateConnectionBindings` reads that descriptor, the mapping and the stable
   reference assembly, then generates `ConnectionBindings.g.cs`. Roslyn symbols
   validate stable constructor members, types and case coverage.

After restore, the schema-only phase can also run independently:

```sh
dotnet msbuild Jondo.Unity.Protocol.Connection.Wire/Jondo.Unity.Protocol.Connection.Wire.csproj -target:Protobuf_Compile
```

The conversion tool's CLI is:

```text
protocolgenerator <descriptor.pb> <mapping.json> <stable.dll> <output.cs>
```

After the default Debug build above, rerun just the conversion generator with:

```sh
dotnet run --project Jondo.Unity.ProtocolGenerator/Jondo.Unity.ProtocolGenerator.csproj --no-build -- Jondo.Unity.Protocol.Connection.Wire/obj/Debug/net10.0/connection.pb Jondo.Unity.Protocol.Connection.Wire/Mappings/connection-3.6.10.11.json Jondo.Unity.Protocol.Connection/bin/Debug/net10.0/Jondo.Unity.Protocol.Connection.dll Jondo.Unity.Protocol.Connection.Wire/obj/Debug/net10.0/ConnectionBindings.g.cs
```

The arguments are, in order, the schema descriptor set, semantic mapping,
compiled stable assembly, and generated C# destination. This invocation does not
run `protoc` or compile the generated C#. Build the wire project afterwards to
compile it. For a Release build, add `--configuration Release` to `dotnet run`
and use the corresponding Release input/output paths. With `--artifacts-path`,
use the relocated paths from that build instead of the default paths above.

It validates the complete mapping before replacing an output. Mapping failures
leave independently generated wire types available for inspection. Changing the
schema, mapping, stable assembly or generator invalidates conversion generation.

## Mapping contract

- `messages` bind a wire alias to a stable record or abstract case type.
- `fields` bind wire fields to stable constructor members.
- `oneofs` bind a wire group to an abstract stable member; `$self` maps the whole
  wire message to a stable case type. Cases may wrap an enum/message in a
  single-member stable record.
- `enums` bind wire value names to stable enum members explicitly. Stable enum
  numeric values are not protobuf values (`Online`, for example, maps to wire 0).
- `ignoredWireFields` deliberately omits declared, unmapped fields from stable
  objects and leaves them at protobuf defaults on output. The 3.6.10.11 mapping omits
  identification field 5, authentication-error field 3 and server-list field 3.
- All stable constructor members and concrete case types need bindings. Missing
  aliases/members, type or repetition mismatches, lost optional presence,
  duplicate identities/keys, unknown policies and incomplete cases/enums fail generation.

The current generator supports the connection schema's messages, enums, repeated
`IReadOnlyList<T>` values, required/nullable messages, optional value scalars and
required oneofs. Unsupported shapes such as maps are reported, not guessed.

Wire parsing uses standard protobuf unknown-field retention. Raw messages remain
available in `Wire.Messages` and through `ConnectionBindings.DecodeWire`. Stable
conversion ignores explicitly unmapped fields and rejects missing required cases
or unmapped enum values. Projection to stable types is intentionally not a
lossless wire round trip.

### Worked field and enum mapping

These excerpts come from the 3.6.10.11 schema, stable types and mapping. They show
one part of the full mapping; the referenced category type has its own binding.

Wire schema:

```proto
message miw {
  int32 gfhi = 1;
  miu gfhj = 2;
  mhf gfhk = 3;
}
enum miu {
  fgox = 0;
  fgoy = 1;
}
```

Hand-written stable types:

```csharp
public sealed record Server(int Id, ConnectionServerStatus Status, ServerCategory Category);
public enum ConnectionServerStatus { Offline, Online }
```

Semantic mapping excerpt:

```json
{
  "messages": {
    "miw": {
      "name": "Server",
      "fields": { "gfhi": "Id", "gfhj": "Status", "gfhk": "Category" }
    }
  },
  "enums": {
    "miu": {
      "name": "ConnectionServerStatus",
      "values": { "fgox": "Online", "fgoy": "Offline" }
    }
  }
}
```

On input, `miw.gfhi` becomes `Server.Id`, and `miw.gfhj = fgox` (wire value 0)
becomes `ConnectionServerStatus.Online` (stable value 1). Output conversion applies
the reverse bindings. Neither the stable record nor the JSON mapping repeats the
field tags: the descriptor supplies those. Changing a wire alias requires updating
the schema and mapping; changing a stable member requires updating its C# definition
and mapping. The generator rejects incompatible types or incomplete construction.

### Worked oneof mapping

The schema's `mim.gfgn` oneof represents server-selection success or error. Its
binding is:

```json
{
  "name": "SelectServerResponse",
  "oneofs": {
    "gfgn": {
      "member": "Result",
      "cases": {
        "gfgk": { "type": "SelectServerSuccess" },
        "gfgl": { "type": "SelectServerError", "member": "Reason" }
      }
    }
  }
}
```

The `gfgk` message branch converts directly to the stable `SelectServerSuccess`
record. The `gfgl` enum branch converts to `SelectServerErrorReason` and is wrapped
in `SelectServerError(Reason)`. Both are cases of the abstract `SelectServerResult`
stored in `SelectServerResponse.Result`. The message and enum payloads have their
own bindings elsewhere in the full mapping. An unset oneof is rejected because
the stable result requires a case; explicitly selecting an error with wire value
zero still produces the error case. Output conversion selects the wire branch
from the concrete stable result type.

## Runtime behavior

`IConnectionCodec.Decode` and `Encode` consume/produce payloads without length
prefixes. The generated root binding owns the obfuscated root alias; transport
does not select it.

Each socket has its own `ConnectionService`. Identification resolves its account;
selection must follow identification and name a joinable server. A successful
selection issues a single-use ticket and returns `Reply(..., CloseAfterReply=true)`;
the owning socket closes after writing the redirect. Ping returns Pong, including
before authentication. Other represented requests are ignored until implemented.
Invalid protobuf or missing semantic cases close the connection. Service/storage
failures are not swallowed as decoder errors.

The request/response string previously labelled `Lang` is an opaque correlation
ID and is echoed per request. The launch registry supplies ticket language, with
Spanish as the normal fallback. Subscription formatting, the seven categories of
five character slots, character-date fallback, zero-based wire breeds, ticket
binding and the duplicate game-port redirect are preserved. Database HTTP status
values are not cast into connection enums: connection replies retain their former
default Online/Accessible representation, while HTTP reports availability and the
backend enforces `Joinable` before issuing tickets.

### Server categories

`Server.Category` and `ServerSlots.Category` share the following explicit bindings.
Database `DbServer.Type` uses the same category numbers; the backend translates them
to stable members before encoding.

| Wire/database value | Stable member |
|---:|---|
| 0 | `MonoAccount` |
| 1 | `Classic` |
| 2 | `PioneerMonoAccount` |
| 3 | `PioneerClassic` |
| 4 | `Tournament` |
| 5 | `Epic` |
| 6 | `Temporis` |

These names follow the 3.6.10.11 client's behavior. `GetServerTypeName` (RVA
`0x4a8f4c0`) uses `ui.server.monoaccount` for 0/2 and `ui.server.pioneer` for
2/3. `GetServerTypeRules` (`0x4a8f8d0`) assigns classic rules to 0..3 and
tournament/epic/Temporis rules to 4/5/6. `ServerInformation.get_isMono`
(`0x4a92dd0`) independently recognizes 0/2. Category zero is a real mono-account
category, including when proto3 omits its default-valued wire field.

The beta connection protocol had separate `type` and `mono_account` fields.
Beta's game-protocol `ServerType` is a distinct enum with different ordinals:
Undefined=0, Classical=1, Kolizeum=2, Tournament=3, Epic=4, Temporis=5.
The connection categories combine ruleset, mono-account and Pioneer distinctions.
Each category retains five character slots in the existing reply projection.

## Verification

```sh
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
```

The first suite executes generated conversions against synthetic schemas (including
rotated aliases/tags), checks invalid mappings and presence, and verifies the pinned
schema/codec. The second drives the real framed transport over ephemeral TCP ports:
fragmented identification, coalesced requests, authentication/selection rejections,
account isolation, redirect followed by EOF, production ticket language/single use,
and byte compatibility with the old reply construction. Neither suite requires a
client installation or world database. CI runs both on Linux and Windows; the full
existing suite remains a Windows check. A real-client server-selection/game-handoff
smoke test completes deployment verification.

# Game protocol tests

This cross-platform .NET 10 xUnit project verifies Game codecs and generated
envelope/Any dispatch. It needs neither an installed client nor a world database.
See `../docs/game-protocol.md`. Commands below run from the repository root.

## Test ownership

- `CodecTests.cs` checks the production `3.6.10.10` profile using independently
  constructed envelope/payload bytes and semantic assertions. This same profile
  supports `3.6.10.11`; do not introduce a duplicate profile for that client patch.
- `EnvelopeGeneratorTests.cs` compiles and executes generated code through Roslyn.
  `Fixtures/GameFixture.proto` and `Fixtures/Stable.cs` supply synthetic wire and
  semantic shapes. Rotated aliases and tags must work through mapping changes.
- General conversion-generator coverage belongs in
  `Jondo.Unity.Protocol.Connection.Tests/GeneratorTests.cs`; Game envelope and
  endpoint-dispatch coverage belongs here.
- Import normalization/reference/provenance tests live in
  `Jondo.Unity.Protocol.Game.Wire/Import/test_import.py` and use temporary inputs.
- Session delivery, map audiences, logger fallback/redaction and live routing
  belong in `Jondo.Unity.Server.Tests`. Database-backed movement/instance checks
  belong in `Jondo.Unity.Tests` with the appropriate shared-state collection.

## Required invariants

- Use independent expected bytes or semantic assertions: a codec round trip alone
  cannot establish wire compatibility. Use invented identifiers and credentials.
- Preserve request/response kind and signed UIDs, including zero and negative
  values. Events carry no UID; mismatched envelope/payload kinds must fail.
- Exercise malformed input, missing content, unknown aliases and unsupported
  semantic values without conflating decode failures with handler failures.
- Raw decoding must retain unmapped declared fields and protobuf unknown fields.
  Unmapped fields/whole oneofs need no ignore lists; outbound stable conversion
  leaves them default/unset unless explicitly defaulted.
- Keep stable coverage strict. Missing stable bindings and unsupported branches
  within mapped enums/oneofs remain errors.
- Preserve optional zero versus absence, explicit absent cases, present-empty
  messages and zero-valued oneof selections when adding relevant fixtures.
- Generated fixture code belongs in build output. Change source fixtures/mappings,
  not generated C#. Keep tests runnable with relocated artifact directories.

## Verification

```sh
dotnet test Jondo.Unity.Protocol.Game.Tests/Jondo.Unity.Protocol.Game.Tests.csproj
```

For shared generator or import changes, also run:

```sh
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
python3 Jondo.Unity.Protocol.Game.Wire/Import/test_import.py
```

CI runs the protocol suites on Linux and Windows, including relocated artifacts
on Linux. The server's publish gate also runs this suite.

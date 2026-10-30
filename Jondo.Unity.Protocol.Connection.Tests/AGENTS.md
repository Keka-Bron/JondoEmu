# Connection protocol and generator tests

This cross-platform .NET 10 xUnit project verifies the codec and conversion
generator. It needs neither a client installation nor a world database. See
`../docs/connection-protocol.md` for the protocol workflow. Commands below run from
the repository root.

## Test ownership

- `GeneratorTests.cs` validates mappings and uses Roslyn to compile and execute
  generated conversions. `Fixtures/Conversion.proto` and `Fixtures/Stable.cs` are
  synthetic inputs with independent wire and semantic representations.
- `CodecTests.cs` checks the production schema/codec, including comparison with
  the extracted `datos/protocolo_conexion_3.6.10.11.proto`, sanitized request/reply
  fixtures, presence, enum remapping, and raw unknown-field handling.
- The extracted compatibility reference lacks oneof/optional metadata; compare
  its message fields and referenced enums, while checking presence separately.
- Keep generator behavior tests synthetic so rotated aliases/tags prove the
  generator is mapping-driven. Exercise generated code, not just source strings.
- Use independent expected bytes or semantic assertions for compatibility checks.
  A round trip through the same encoder/decoder alone cannot prove wire compatibility.
- Cover optional zero versus absence, empty versus missing messages, zero-valued
  oneof cases, required-message/case errors, and unsupported enum values when relevant.
- Use invented account, character, token, and certificate data in fixtures.
- TCP service behavior, account isolation, and production ticket-registry tests
  belong in `Jondo.Unity.Server.Tests`.

## Verification

```sh
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
```

This builds the stable types, generator, wire types, and conversions automatically.
Generated fixture code remains in build output. CI runs this suite on Linux and Windows.

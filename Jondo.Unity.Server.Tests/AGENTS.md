# Connection server integration tests

This xUnit project tests the connection service and framed transport against the
actual server assembly. It targets `net10.0` on Linux and `net10.0-windows` on Windows
to match the server. See `../docs/connection-protocol.md` for architecture and test
coverage. Commands below run from the repository root.

## Test ownership and isolation

- `ConnectionTests.cs` exercises the service/transport in
  `Jondo.Unity.Server/Network/Connection/` using the real connection codec and
  existing framing helpers.
- Use injected `IConnectionBackend` implementations for account/server behavior.
  Keep the suite runnable without real accounts, installed clients, or world databases.
- TCP tests use loopback listeners with ephemeral ports, bounded waits, and disposal
  that observes the server task. Cover fragmentation/coalescing at the transport boundary.
- Preserve per-socket account isolation, selection-after-identification, joinability
  checks, exact per-request correlation IDs, and redirect followed by EOF.
- Focused production-backend tests may use the launch/ticket registries with unique,
  invented identifiers. Remove registered launches and redeem issued tickets so
  static state does not leak into other tests.
- Ticket language comes from the launch, not the correlation ID. Verify tickets
  remain bound to the account/server and can only be redeemed once.
- Keep service/storage failure assertions distinct from codec failure assertions.
- Use independent legacy-byte construction to pin reply compatibility. Schema and
  generic generator tests belong in `Jondo.Unity.Protocol.Connection.Tests`.

## Verification

```sh
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
```

For changes spanning the codec boundary, also run:

```sh
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
```

Both suites run in Linux and Windows CI. The larger `Jondo.Unity.Tests` suite is a
separate Windows-only check.

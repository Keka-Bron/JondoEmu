# Server integration and desktop tests

This xUnit project tests the connection service and framed transport against the
actual server assembly. It also checks the server's Avalonia desktop, settings editor,
log presentation and process-lifetime coordination. It targets `net10.0` on Linux and
Windows. See `../docs/connection-protocol.md` and `../docs/server-desktop.md` for
architecture and coverage. Commands below run from the repository root.

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
- Desktop tests use Avalonia.Headless with real Skia drawing and an application that
  loads the production Fluent theme without starting world services. Inject settings
  snapshots and shutdown callbacks; do not write real operator configuration or call
  `Program.Main` from a desktop test.

## Verification

```sh
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
```

For changes spanning the codec boundary, also run:

```sh
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
```

Both suites and the larger `Jondo.Unity.Tests` suite run in Linux and Windows CI.
The general suite additionally needs the world database extracted from `datos/world.zip`
into `bases/` for database-backed coverage; these focused suites do not.

# Build-time protocol targets

The default server build uses the Game `3.6.10.10` and Connection `3.6.10.11`
profiles. To build against the `3.8.1.2` beta protocol, run:

```sh
dotnet build Jondo.Unity.Server -p:JondoProtocolTarget=3.8.1.2
```

This is one MSBuild property for both wire projects. It selects checked-in
schema and binding pairs and defines `JONDO_PROTOCOL_3_8_1_2` for the few C# paths
and model shapes that differ. Each binary contains only the selected active
protocol. `GameCodec.JondoVersion` reports the compiled profile. The beta build uses
`host-artifacts` by default and suffixes its project directories with
`.protocol-3.8.1.2`, so it can share an `--artifacts-path` with the default build.

The November profile covers Jondo's mapped semantic messages. Legacy packet
builders, raw routing and captures still describe the default client. The beta
server skips requests without a typed handler rather than routing their aliases
to default-client handlers. The
November profile has no binding for the tag-storage and configuration events,
so the typed entry sequence omits them. Character breed and sub-entity binding
point category are now `int32` fields without reviewed target value tables; they
remain raw-only and read as absent in the typed projection. Historical database
shortcut payloads still use their original format.

To run against the corresponding client catalogues, see [November beta assets](beta-assets.md).

## Verification and publish

```sh
dotnet msbuild Jondo.Unity.Server/Jondo.Unity.Server.csproj \
  -t:RunTestsBeforePublish -p:JondoProtocolTarget=3.8.1.2

dotnet publish Jondo.Unity.Server -c Release \
  -p:JondoProtocolTarget=3.8.1.2 -p:SkipRootDeploy=true
```

The November publish gate runs the Connection tests and focused Game and server
tests against the selected bindings, including the database-backed `TutorialFlowTests`.
With the world database installed, these drive character creation and first selection,
quest acceptance, optional movement confirmation, exit rewards and destination reload
through the selected Game codec and production dispatcher. The default publish gate still runs all
four suites, including the general suite and legacy wire fixtures.
`SkipRootDeploy=true` leaves the published server in its distinct artifacts
directory instead of copying it over a root deployment.

## Maintaining the checked-in inputs

The source of truth for November is `Profiles/3.8.1.2/` in each wire project:
`game.proto` and `game.json` in `Jondo.Unity.Protocol.Game.Wire`, and
`connection.proto` and `connection.json` in `Jondo.Unity.Protocol.Connection.Wire`.
Edit the `.proto` for wire structure (tags, types, oneofs and presence). Edit
the matching `.json` for reviewed bindings to Jondo's C# models. Leave fields
with unresolved meanings raw-only. Run the November test gate above: the build
compiles the schemas and checks the bindings against the descriptors and stable
types. No external mapping checkout is needed.

`Jondo.Unity.Protocol.Game.Wire/Storage/shortcut.proto` and `shortcut.json` are
a frozen pair for reading existing shortcut saves. Do not retarget them when
changing a wire profile. The default target reads the original August wire type;
the November target generates its legacy reader from this pair. Both must decode
the same historical bytes until those saves have been migrated.

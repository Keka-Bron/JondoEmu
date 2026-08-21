# Versioned client data and protocol compatibility

Every extracted client build lives in its own directory:

```text
client_data/
  3.6.10.10/
  3.6.10.11/
  3.6.20.10/
```

Each version contains independent `catalogs/`, `world/`, `server/`, `protocol/`,
`mechanics/`, and optional translated reference data. Never overwrite an older snapshot.

At startup Jondo selects the newest `server/manifest.json` whose `clientVersion` matches its
directory and whose `serverProtocolVersion` matches the compiled server protocol. Set
`JONDO_CLIENT_DATA_VERSION` to select a particular compatible snapshot. The server logs both the
active data version and compiled protocol version.

This is intentionally a compatibility gate, not unsafe automatic patching. A newer Dofus client
may have changed protobuf fields, message ordering, server validation or combat behaviour. Its
static data can be extracted immediately, but it becomes active only after the manifest declares
the server-protocol compatibility and the protocol tests pass.

## Protocol catalogue

`protocol/game-protocol.proto` and `protocol/opcode-index.json` are copied from the exact client
extraction by `py tools/build_protocol_data_snapshot.py`. `packet-policy.json` holds only
evidence-backed telemetry dispositions such as known-no-reply C2S messages. The server loads that
policy dynamically; changing it cannot make a state-changing packet execute.

## Better packet evidence

`UnknownPackets` keeps one deduplicated replay sample per wire shape. `PacketOccurrences` keeps a
compact ordered trail of every later occurrence: time, map, character, phase, request id, payload
hash, decoded shape and error—without duplicating raw payload blobs. Use:

```powershell
py tools/export_packet_evidence.py --id 184 --out logs/packet-184-evidence.json
```

An implementation still requires an isolated client action plus its S2C result. Static schemas
make decoding and change detection automatic; they do not contain the authoritative server action.

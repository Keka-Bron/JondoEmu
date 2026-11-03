using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Jondo.Unity.Protocol.Game.Wire;

public sealed record GameWireEnvelope(GameMessageKind Kind, int? Uid, Any Content)
{
    public string Alias => Content.TypeUrl[(Content.TypeUrl.LastIndexOf('/') + 1)..];
}

public sealed class GameCodec : IGameCodec
{
    /// <summary>Canonical Game protocol version; client 3.6.10.11 uses the identical protocol.</summary>
    public const string JondoVersion = "3.6.10.10";
    public GameFrame Decode(ReadOnlySpan<byte> payload) => GameBindings.Decode(payload);
    public byte[] Encode(GameFrame message) => GameBindings.Encode(message);
    public IMessage DecodeWire(ReadOnlySpan<byte> payload) => GameBindings.DecodeWire(payload);
    public GameWireEnvelope ReadEnvelope(ReadOnlySpan<byte> payload) => GameBindings.ReadEnvelope(payload);
    public IMessage DecodeRawPayload(string alias, ReadOnlySpan<byte> payload) => GameBindings.DecodeRawPayload(alias, payload);
}

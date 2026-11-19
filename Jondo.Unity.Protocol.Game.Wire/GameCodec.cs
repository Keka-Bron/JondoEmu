using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Jondo.Unity.Protocol.Game.Wire;

public sealed record GameWireEnvelope(GameMessageKind Kind, int? Uid, Any Content)
{
    public string Alias => Content.TypeUrl[(Content.TypeUrl.LastIndexOf('/') + 1)..];
}

public sealed class GameCodec : IGameCodec
{
    /// <summary>Game wire profile selected at build time.</summary>
#if JONDO_PROTOCOL_3_8_1_2
    public const string JondoVersion = "3.8.1.2";
#else
    public const string JondoVersion = "3.6.10.10";
#endif
    public GameFrame Decode(ReadOnlySpan<byte> payload) => GameBindings.Decode(payload);
    public byte[] Encode(GameFrame message) => GameBindings.Encode(message);
    public IMessage DecodeWire(ReadOnlySpan<byte> payload) => GameBindings.DecodeWire(payload);
    public GameWireEnvelope ReadEnvelope(ReadOnlySpan<byte> payload) => GameBindings.ReadEnvelope(payload);
    public IMessage DecodeRawPayload(string alias, ReadOnlySpan<byte> payload) => GameBindings.DecodeRawPayload(alias, payload);
}

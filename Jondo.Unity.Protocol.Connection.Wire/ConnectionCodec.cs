namespace Jondo.Unity.Protocol.Connection.Wire;

/// <summary>Version-independent transport entry point; the generated root binding owns its wire alias.</summary>
public sealed class ConnectionCodec : IConnectionCodec
{
    public ConnectionMessage Decode(ReadOnlySpan<byte> payload) => ConnectionBindings.Decode(payload);
    public byte[] Encode(ConnectionMessage message) => ConnectionBindings.Encode(message);
}

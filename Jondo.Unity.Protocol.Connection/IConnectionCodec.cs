namespace Jondo.Unity.Protocol.Connection;

/// <summary>Encodes protobuf payloads, without the transport's varint length prefix.</summary>
public interface IConnectionCodec
{
    ConnectionMessage Decode(ReadOnlySpan<byte> payload);
    byte[] Encode(ConnectionMessage message);
}

/// <summary>A well-formed wire value cannot be represented by the stable protocol.</summary>
public sealed class ConnectionConversionException(string message) : Exception(message);

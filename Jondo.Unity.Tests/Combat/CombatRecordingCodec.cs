using System;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

internal sealed class CombatRecordingCodec(Action<GameFrame> record) : IGameCodec
{
    private static readonly byte[] Marker = [0x71];

    public byte[] Encode(GameFrame frame)
    {
        record(frame);
        return Marker;
    }

    public GameFrame Decode(ReadOnlySpan<byte> payload)
        => throw new NotSupportedException("Combat flow tests dispatch semantic requests directly.");

    internal static Task RequireTypedPacketAsync(byte[] packet)
    {
        Assert.Same(Marker, packet);
        return Task.CompletedTask;
    }

    // Compare existing August captures by their reviewed fields, allowing protobuf field
    // reordering and intentional omissions such as the legacy author on sequence start.
    internal static byte[] MappedCapture(string opcode, byte[] body)
    {
        var codec = new GameCodec();
        var frame = codec.Decode(ConnectionProtocol.Push(opcode, body));
        return ConnectionProtocol.ReadPayload(codec.Encode(frame), opcode)!;
    }
}

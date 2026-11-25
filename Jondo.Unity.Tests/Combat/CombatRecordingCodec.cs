using System;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
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
}

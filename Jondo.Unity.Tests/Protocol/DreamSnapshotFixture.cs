using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;

namespace Jondo.Unity.Tests;

internal static class DreamSnapshotFixture
{
    // Compare the semantic producer through the production codec against the captured payloads.
    internal static byte[] Payload(this InfiniteDreamStateEvent snapshot)
    {
        var codec = new GameCodec();
        return codec.ReadEnvelope(codec.Encode(new(snapshot))).Content.Value.ToByteArray();
    }
}

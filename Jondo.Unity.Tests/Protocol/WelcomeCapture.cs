using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol;

/// <summary>Records actual welcome delivery through an opaque, session-owned codec.</summary>
internal sealed class WelcomeCapture
{
    internal sealed record RawPacket(byte[] Bytes);

    internal List<object> Deliveries { get; } = [];
    internal IEnumerable<GameFrame> Frames => Deliveries.OfType<GameFrame>();
    internal IEnumerable<GamePayload> Messages => Frames.Select(frame => frame.Content);
    internal Type[] DeliveryTypes => Deliveries.Select(delivery => delivery is GameFrame frame
        ? frame.Content.GetType() : typeof(RawPacket)).ToArray();

    internal static async Task<WelcomeCapture> SendAsync(IReadOnlyList<DatabaseManager.DbCharacter> characters,
        bool withList = true, string language = "es")
    {
        var capture = new WelcomeCapture();
        var codec = new RecordingCodec();
        var session = new GameSession(codec, packet =>
        {
            capture.Deliveries.Add(codec.Pending.Remove(packet, out var frame) ? frame : new RawPacket(packet));
            return Task.CompletedTask;
        });
        session.BindAccount(8_919_099_003, 290, language);
        using var context = SessionContext.Push(session);
        await ConnectionProtocol.SendWelcomeAsync(session, characters, withList).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(codec.Pending);
        return capture;
    }

    private sealed class RecordingCodec : IGameCodec
    {
        internal Dictionary<byte[], GameFrame> Pending { get; } = [];
        public byte[] Encode(GameFrame frame)
        {
            // A fresh token ties each delivered packet to its semantic frame by identity.
            byte[] token = [0xfe, 0xff];
            Pending.Add(token, frame);
            return token;
        }
        public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new Exception("Unexpected welcome decode");
    }
}

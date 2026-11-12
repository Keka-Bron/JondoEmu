using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol;

/// <summary>Records actual welcome delivery through an opaque, session-owned codec.</summary>
internal sealed class WelcomeCapture
{
    internal List<GameFrame> Deliveries { get; } = [];
    internal IEnumerable<GameFrame> Frames => Deliveries;
    internal IEnumerable<GamePayload> Messages => Frames.Select(frame => frame.Content);
    internal Type[] DeliveryTypes => Deliveries.Select(frame => frame.Content.GetType()).ToArray();

    internal static async Task<WelcomeCapture> SendAsync(IReadOnlyList<DbCharacter> characters,
        bool withList = true, string language = "es")
    {
        var capture = new WelcomeCapture();
        var codec = new RecordingCodec();
        var session = new GameSession(codec, packet =>
        {
            Assert.True(codec.Pending.Remove(packet, out var frame), "Welcome delivery bypassed the owning codec.");
            capture.Deliveries.Add(frame!);
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

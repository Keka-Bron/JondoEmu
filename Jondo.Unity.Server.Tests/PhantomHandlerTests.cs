using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class PhantomHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Phantom_initial_notification_propagates_failure_without_repeating_on_the_same_session(bool codecFailure)
    {
        const long id = 8_854_000_001, marker = 8_854_000_002;
        var failure = new IOException("test phantom send failure");
        var codec = new RecordingCodec(codecFailure ? failure : null);
        int writes = 0;
        var session = new GameSession(codec, _ => { writes++; return Task.FromException(failure); });
        session.State.CharacterId = id;
        Phantoms.Become(id, marker);
        try
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => PhantomHandler.OnMapLoadedAsync(session)));
            await PhantomHandler.OnMapLoadedAsync(session);
            var frame = Assert.Single(codec.Frames);
            Assert.Null(frame.Uid);
            Assert.Equal(new CharacterLifeStatusEvent(CharacterLifeStatus.Phantom, marker), frame.Content);
            Assert.Equal(codecFailure ? 0 : 1, writes);
        }
        finally { Phantoms.Rise(id); }
    }

    private sealed class RecordingCodec(Exception? failure) : IGameCodec
    {
        public List<GameFrame> Frames { get; } = [];
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (failure != null) throw failure;
            return [0xAB];
        }
        public GameFrame Decode(ReadOnlySpan<byte> packet) => throw new InvalidOperationException("A send cannot decode.");
    }
}

using System.Collections.Concurrent;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class GameMessagingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Typed_sends_use_the_session_codec_and_preserve_response_ids(int uid)
    {
        var codec = new CountingCodec();
        using var target = new Recipient(12_000_001, codec);
        await target.Session.SendAsync(new PongEvent(true));
        await target.Session.SendAsync(new MapMovementConfirmedResponse(), uid);
        var frames = target.Packets.ToArray();
        var eventFrame = new GameCodec().Decode(frames[0]);
        Assert.Equal(new PongEvent(true), eventFrame.Content);
        Assert.Null(eventFrame.Uid);
        var response = new GameCodec().Decode(frames[1]);
        Assert.IsType<MapMovementConfirmedResponse>(response.Content);
        Assert.Equal(uid, response.Uid);
        Assert.Equal(2, codec.Encodes);

        byte[] legacy = [1, 2, 3];
        await target.Session.SendAsync(legacy);
        Assert.Same(legacy, target.Packets.Last());
        Assert.Equal(2, codec.Encodes);
    }

    [Fact]
    public async Task Map_sends_include_self_and_use_captured_map_not_ambient_or_later_sender_state()
    {
        const long mapId = 12_000_002;
        var codec = new CountingCodec();
        using var sender = new Recipient(mapId, codec);
        using var observer = new Recipient(mapId);
        using var elsewhere = new Recipient(mapId + 1);
        using var notInWorld = new Recipient(mapId, enterWorld: false);
        var map = sender.Session.Map;
        var message = new GameMapMovementEvent([1, 2], sender.Session.CharacterId, false);
        using (SessionContext.Push(elsewhere.Session))
        {
            // A recipient can hold its game-state lock and still receive notifications.
            using var turn = await observer.Session.EnterTurnAsync();
            Assert.Equal(2, await map.SendAsync(message).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(elsewhere.Session, SessionContext.Actual);
        }
        Assert.Single(sender.Packets);
        Assert.Single(observer.Packets);
        Assert.Same(sender.Packets.Single(), observer.Packets.Single());
        Assert.Empty(elsewhere.Packets);
        Assert.Empty(notInWorld.Packets);
        Assert.Equal(1, codec.Encodes);

        sender.Session.State.MapId = mapId + 1;
        Assert.Equal(mapId, map.MapId);
        Assert.Equal(1, await map.SendAsync(message));
        Assert.Single(sender.Packets);
        Assert.Equal(2, observer.Packets.Count);
        Assert.Empty(elsewhere.Packets);
        Assert.Equal(0, await map.SendAsync(message, exceptSessionId: observer.Session.Id));
    }

    [Fact]
    public async Task Fight_filter_is_explicit_and_omitting_it_preserves_map_wide_delivery()
    {
        const long map = 12_000_004;
        using var roleplay = new Recipient(map);
        using var fighter = new Recipient(map);
        fighter.Session.State.FightId = 99;
        Assert.Equal(2, await roleplay.Session.Map.SendAsync(new PongEvent(false)));
        Assert.Equal(1, await roleplay.Session.Map.SendAsync(new PongEvent(false), fightId: 0));
        Assert.Equal(1, await roleplay.Session.Map.SendAsync(new PongEvent(false), fightId: 99));
        Assert.Equal(2, roleplay.Packets.Count);
        Assert.Equal(2, fighter.Packets.Count);
    }

    [Fact]
    public async Task Fanout_runs_concurrently_and_one_failure_does_not_remove_or_block_other_recipients()
    {
        const long map = 12_000_005;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slow = new Recipient(map, send: _ => release.Task);
        using var fast = new Recipient(map, send: _ => { arrived.TrySetResult(); return Task.CompletedTask; });
        using var broken = new Recipient(map, send: _ => throw new IOException("test transport failure"));
        var sending = slow.Session.Map.SendAsync(new PongEvent(true));
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(sending.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(2, await sending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(SessionRegistry.TryGet(broken.Session.Id, out _));
    }

    internal sealed class CountingCodec : IGameCodec
    {
        private readonly GameCodec _codec = new();
        internal int Encodes;
        internal int Decodes;
        public GameFrame Decode(ReadOnlySpan<byte> payload)
        {
            Interlocked.Increment(ref Decodes);
            return _codec.Decode(payload);
        }
        public byte[] Encode(GameFrame message)
        {
            Interlocked.Increment(ref Encodes);
            return _codec.Encode(message);
        }
    }

    internal sealed class Recipient : IDisposable
    {
        private static long _nextId = 19_000_000;
        internal ConcurrentQueue<byte[]> Packets { get; } = new();
        internal GameSession Session { get; }

        internal Recipient(long mapId, IGameCodec? codec = null, bool enterWorld = true, Func<byte[], Task>? send = null)
        {
            Session = new GameSession(codec ?? new GameCodec(), send ?? (bytes => { Packets.Enqueue(bytes); return Task.CompletedTask; }));
            long id = Interlocked.Increment(ref _nextId);
            Session.BindAccount(id, 1);
            Session.State.CharacterId = id;
            Session.State.MapId = mapId;
            if (enterWorld) Session.EnterWorld();
            Assert.True(SessionRegistry.Register(Session));
        }

        public void Dispose() => SessionRegistry.Unregister(Session);
    }
}

using System.Collections.Concurrent;
using System.Reflection;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat;

public sealed class FightMarkerTests
{
    private const long Id = 7_100_041, Map = 990_100_091, Me = 990_000_041;
    private static readonly GameCodec Wire = new();
    private static readonly ConcurrentDictionary<long, FightInstance> Active =
        (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Theory]
    [InlineData(FightInstance.Azules, false, 288)]
    [InlineData(FightInstance.Rojos, true, 0)]
    public async Task Cell_markers_reach_the_senders_team_after_the_pending_sequence(int team, bool ongoing, int cell)
    {
        using var sender = new Recipient(Me);
        using var failing = new Recipient(Me + 1, failDelivery: true);
        using var ally = new Recipient(Me + 2);
        using var enemy = new Recipient(Me + 3);
        using var spectator = new Recipient(Me + 4, participant: false);
        using var bystander = new Recipient(Me + 5, participant: false);
        using var gone = new Recipient(Me + 6);
        spectator.Session.State.SpectatingFightId = Id;
        var spectators = (ConcurrentDictionary<long, ConcurrentDictionary<long, byte>>)typeof(FightHandler)
            .GetField("_spectators", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        var fight = new FightInstance(Id, Map);
        // The opponent wins initiative so the ongoing marker is sent out of turn.
        Add(fight, enemy, FightInstance.Contrario(team), initiative: 100);
        foreach (var recipient in new[] { sender, failing, ally, gone }) Add(fight, recipient, team);
        fight.Gone.Add(gone.Session.CharacterId);
        if (ongoing)
        {
            fight.StartFight();
            Assert.Equal(enemy.Session.CharacterId, fight.CurrentFighter!.Id);
        }

        bool opened = false;
        fight.SequenceToOpen = () => { opened = true; return Task.CompletedTask; };
        sender.BeforeEncode = ally.BeforeEncode = () => Assert.True(opened);
        Assert.True(Active.TryAdd(Id, fight));
        try
        {
            spectators[Id] = new([new(spectator.Session.CharacterId, 0)]);
            using var ambient = SessionContext.Push(enemy.Session);
            await Mark(sender, cell);
            foreach (var recipient in new[] { sender, ally, failing })
            {
                var frame = Assert.Single(recipient.Frames);
                Assert.Equal(new ShowCellEvent(Me, cell), frame.Content);
                Assert.Null(frame.Uid);
            }
            Assert.Equal(1, sender.Delivered);
            Assert.Equal(1, ally.Delivered);
            Assert.Equal(0, failing.Delivered);
            Assert.Null(fight.SequenceToOpen);

            await Mark(spectator, 21);
            await Mark(bystander, 22);
            Assert.Single(sender.Frames);
            Assert.Single(ally.Frames);
            Assert.All(new[] { enemy, spectator, bystander, gone }, recipient => Assert.Empty(recipient.Frames));
        }
        finally
        {
            spectators.TryRemove(Id, out _);
            Active.TryRemove(Id, out _);
        }
    }

    [Fact]
    public async Task A_queued_marker_does_not_follow_the_sender_into_a_replacement_fight()
    {
        using var sender = new Recipient(Me);
        using var ally = new Recipient(Me + 2);
        var original = new FightInstance(Id, Map);
        var replacement = new FightInstance(Id, Map);
        Add(original, sender, FightInstance.Azules);
        Add(replacement, sender, FightInstance.Rojos);
        Add(replacement, ally, FightInstance.Rojos);
        Assert.True(Active.TryAdd(Id, original));
        try
        {
            await original.Turno.WaitAsync();
            Task queued;
            try
            {
                queued = Mark(sender, 288);
                Assert.False(queued.IsCompleted);
                Assert.True(Active.TryUpdate(Id, replacement, original));
            }
            finally { original.Turno.Release(); }
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(sender.Frames);
            Assert.Empty(ally.Frames);
        }
        finally { Active.TryRemove(Id, out _); }
    }

    private static void Add(FightInstance fight, Recipient recipient, int team, int initiative = 0)
        => fight.Bando(team).Add(new()
        {
            Id = recipient.Session.CharacterId, TeamId = team, CurrentHP = 100, Initiative = initiative,
        });

    private static async Task Mark(Recipient sender, int cell)
    {
        var session = sender.Session;
        using var turn = await session.EnterTurnAsync();
        Assert.Equal(GameEntryResult.Handled, await GameNodeProxy.DispatchPacketAsync(session,
            Wire.Encode(new(new ShowCellRequest(cell), -42)), new GameEntryService(session, new GameEntryBackend()),
            () => throw new InvalidOperationException("Cell markers must never use legacy dispatch.")));
    }

    private sealed class Recipient : IGameCodec, IDisposable
    {
        private readonly byte[] _opaquePacket = [0x71];
        public List<GameFrame> Frames { get; } = [];
        public GameSession Session { get; }
        public Action? BeforeEncode { private get; set; }
        public int Delivered { get; private set; }

        public Recipient(long id, bool failDelivery = false, bool participant = true)
        {
            Session = new(this, packet =>
            {
                if (failDelivery) throw new IOException("transport failed");
                // Checked outside the send callback as the team broadcaster isolates failures.
                if (ReferenceEquals(packet, _opaquePacket)) Delivered++;
                return Task.CompletedTask;
            });
            Session.BindAccount(id + 1_000_000_000, 1);
            Session.State.CharacterId = id;
            Session.State.MapId = Map;
            Session.State.IsInFight = participant;
            Session.State.FightId = participant ? Id : 0;
            Session.EnterWorld();
            Assert.True(SessionRegistry.Register(Session));
        }

        public GameFrame Decode(ReadOnlySpan<byte> packet) => Wire.Decode(packet);

        public byte[] Encode(GameFrame frame)
        {
            BeforeEncode?.Invoke();
            Frames.Add(frame);
            return _opaquePacket;
        }

        public void Dispose() => SessionRegistry.Unregister(Session);
    }
}

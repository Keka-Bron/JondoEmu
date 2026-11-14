using System.Collections.Concurrent;
using System.Reflection;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World;

// Prism changes broadcast to the entire world; the existing conquest collection
// owns that global fanout and the temporary alliance/prism store overrides.
[Collection("conquests")]
public sealed class CollectorPrismPresenceTests : IDisposable
{
    private const long Map = 91_966_001, Owner = 9_966_000_001, Account = 8_966_000_001;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"jondo-actor-presence-{Guid.NewGuid():N}.db");

    public CollectorPrismPresenceTests()
    {
        AllianceStore.ConnectionStringOverride = $"Data Source={_file}";
        TaxCollectorStore.ConnectionStringOverride = $"Data Source={_file}";
        PrismStore.ConnectionStringOverride = $"Data Source={_file}";
        SetMap(838);
    }

    public void Dispose()
    {
        MapManager.Maps.Remove(Map);
        TaxCollectorStore.ConnectionStringOverride = null;
        PrismStore.ConnectionStringOverride = null;
        AllianceStore.ConnectionStringOverride = null;
        TestDatabases.ReleasePool(_file);
        File.Delete(_file);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Placement_spends_the_item_then_sends_own_actor_peers_and_feature_notice_with_own_failure_stopping_fanout(
        bool prism, bool ownFailure)
    {
        MapManager.Initialize();
        Interactives.Initialize();
        var area = MapManager.Maps.Values.First(map => Interactives.IsCapturable(map.SubAreaId)
            && Interactives.AreaOfSubArea(map.SubAreaId) != 45);
        SetMap(area.SubAreaId);
        var alliance = Alliance();
        int template = PlacementTemplate(prism, area.SubAreaId);
        var item = DatabaseManager.AddPieceToInventory(Owner, template, "[]")!;
        Assert.NotNull(item);
        var codec = new RecordingCodec { Fail = ownFailure };
        await using var own = await ClientPipe.OpenAsync(Account, Owner, Map, "Placer", codec);
        using var peer = new Recipient(Owner + 1, Map);
        using var fighter = new Recipient(Owner + 2, Map, fightId: 77);
        using var elsewhere = new Recipient(Owner + 3, Map + 1);
        try
        {
            using (SessionContext.Push(own.Session))
            using (await own.Session.EnterTurnAsync())
            using (await peer.Session.EnterTurnAsync())
            {
                own.Session.State.CellId = 312;
                own.Session.State.Orientation = 7;
                Equipment.Add(item.Uid, template, 1, Equipment.Bag, null);
                var placing = prism
                    ? PrismHandler.TryPlaceAsync(own.ToClient, Equipment.ByUid(item.Uid)!)
                    : TaxCollectorHandler.TryPlaceAsync(own.ToClient, Equipment.ByUid(item.Uid)!);
                if (ownFailure) await Assert.ThrowsAsync<IOException>(() => placing);
                else Assert.True(await placing.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Null(Equipment.ByUid(item.Uid));
            }
            await own.Next("ium");
            await own.Next("iun");
            var actor = Show(Assert.Single(codec.Frames));
            Assert.Equal(312, actor.Disposition.CellId);
            Assert.Equal(Direction.NorthEast, actor.Disposition.Direction);
            Assert.Equal(prism ? PrismStore.OnMap(Map)!.ContextId : TaxCollectorStore.OnMap(Map)!.ContextId, actor.ActorId);
            if (ownFailure)
            {
                Assert.False(own.FromServer.DataAvailable);
                Assert.Empty(peer.Packets);
                Assert.Empty(fighter.Packets);
                Assert.Empty(elsewhere.Packets);
            }
            else
            {
                Assert.Equal(new byte[] { 0x71, 1 }, await Read(own));
                await own.Next(prism ? "ifa" : "hjq");
                Assert.Equal(prism ? new[] { "typed-show", "ifa" } : new[] { "typed-show" }, peer.Labels);
                Assert.Same(codec.Frames[0].Content, Assert.Single(peer.Codec.Frames).Content);
                Assert.Empty(fighter.Codec.Frames);
                Assert.Empty(elsewhere.Codec.Frames);
                Assert.Equal(prism ? new[] { "ifa" } : [], fighter.Labels);
                Assert.Equal(prism ? new[] { "ifa" } : [], elsewhere.Labels);
            }
        }
        finally
        {
            using var db = new SqliteConnection(DatabaseManager.WorldConnectionString);
            db.Open();
            using var clean = db.CreateCommand();
            clean.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $owner;";
            clean.Parameters.AddWithValue("$owner", Owner);
            clean.ExecuteNonQuery();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Fight_survival_restores_a_typed_actor_after_feature_notices_and_isolates_failed_map_recipients(
        bool prism, bool transportFailure)
    {
        var alliance = Alliance();
        var collector = prism ? null : TaxCollectorStore.Place(alliance.Id, Owner, "Owner", Map, 838, 312, 5, 78, 241);
        var standing = prism ? PrismStore.Place(alliance.Id, 838, Map, 312, 5) : null;
        long actorId = standing?.ContextId ?? collector!.ContextId;
        var fight = new FightInstance(7_966_001, Map, Map + 9)
        {
            DefenderLeaderId = actorId, TaxCollectorUuid = collector?.Uuid,
        };
        using var own = new Recipient(Owner, Map);
        using var peer = new Recipient(Owner + 1, Map);
        using var broken = new Recipient(Owner + 2, Map, failTransport: transportFailure);
        broken.Codec.Fail = !transportFailure;
        using var fighter = new Recipient(Owner + 3, Map, fightId: fight.FightId + 1);
        using var elsewhere = new Recipient(Owner + 4, Map + 1);
        using (SessionContext.Push(elsewhere.Session))
        using (await peer.Session.EnterTurnAsync())
        {
            if (prism) await PrismHandler.FightEndedAsync(fight, attackersWon: false);
            else await TaxCollectorHandler.FightEndedAsync(fight, attackersWon: false);
            Assert.Same(elsewhere.Session, SessionContext.Actual);
        }
        Assert.Equal(prism ? new[] { "iev", "typed-show" } : new[] { "hjw", "hkd", "typed-show" }, own.Labels);
        Assert.Equal(actorId, Show(Assert.Single(own.Codec.Frames)).ActorId);
        Assert.Equal(actorId, Show(Assert.Single(peer.Codec.Frames)).ActorId);
        Assert.Same(own.Codec.Frames[0].Content, peer.Codec.Frames[0].Content);
        Assert.Empty(broken.Packets);
        Assert.Single(broken.Codec.Frames);
        Assert.Empty(fighter.Packets);
        Assert.Empty(elsewhere.Packets);
        Assert.True(SessionRegistry.TryGet(broken.Session.Id, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alliance_dissolution_removes_the_actor_only_from_roleplay_then_sends_the_prism_world_notice(bool prism)
    {
        var alliance = Alliance();
        var collector = prism ? null : TaxCollectorStore.Place(alliance.Id, Owner, "Owner", Map, 838, 312, 5, 78, 241);
        var standing = prism ? PrismStore.Place(alliance.Id, 838, Map, 312, 5) : null;
        long actorId = standing?.ContextId ?? collector!.ContextId;
        using var own = new Recipient(Owner, Map);
        using var peer = new Recipient(Owner + 1, Map);
        using var fighter = new Recipient(Owner + 2, Map, fightId: 77);
        using var elsewhere = new Recipient(Owner + 3, Map + 1);
        if (prism) await PrismHandler.AllianceDissolvedAsync(alliance.Id);
        else await TaxCollectorHandler.AllianceDissolvedAsync(alliance.Id);
        Assert.Equal(new ContextRemoveElementEvent(actorId), Assert.Single(own.Codec.Frames).Content);
        Assert.Equal(new ContextRemoveElementEvent(actorId), Assert.Single(peer.Codec.Frames).Content);
        Assert.Equal(prism ? new[] { "typed-remove", "iew" } : new[] { "typed-remove" }, own.Labels);
        Assert.Equal(prism ? new[] { "iew" } : [], fighter.Labels);
        Assert.Equal(prism ? new[] { "iew" } : [], elsewhere.Labels);
        Assert.Null(PrismStore.OnMap(Map));
        Assert.Null(TaxCollectorStore.OnMap(Map));
    }

    [Fact]
    public async Task Collected_collector_sends_collection_before_typed_removal_and_member_list_updates()
    {
        var alliance = Alliance();
        var collector = TaxCollectorStore.Place(alliance.Id, Owner, "Owner", Map, 838, 312, 5, 78, 241)!;
        using var own = new Recipient(Owner, Map);
        using var peer = new Recipient(Owner + 1, Map);
        using (SessionContext.Push(own.Session))
            await TaxCollectorHandler.CollectAsync(null!, ConnectionProtocol.Push("kbu", Pb.New().Str(1, collector.Uuid).Build()));
        Assert.Equal(new[] { "kdc", "typed-remove", "hjt", "hlc" }, own.Labels);
        Assert.Equal(new ContextRemoveElementEvent(collector.ContextId), Assert.Single(own.Codec.Frames).Content);
        Assert.Equal(new[] { "typed-remove" }, peer.Labels);
        Assert.Null(TaxCollectorStore.OnMap(Map));
    }

    [Fact]
    public async Task Defeated_collector_sends_fight_result_before_typed_removal_list_update_and_owner_notice()
    {
        var alliance = Alliance();
        var collector = TaxCollectorStore.Place(alliance.Id, Owner, "Owner", Map, 838, 312, 5, 78, 241)!;
        using var own = new Recipient(Owner, Map);
        using var peer = new Recipient(Owner + 1, Map);
        var fight = new FightInstance(7_966_003, Map, Map + 9) { TaxCollectorUuid = collector.Uuid };
        await TaxCollectorHandler.FightEndedAsync(fight, attackersWon: true);
        Assert.Equal(new[] { "hjw", "typed-remove", "hlc", "lqn" }, own.Labels);
        Assert.Equal(new ContextRemoveElementEvent(collector.ContextId), Assert.Single(own.Codec.Frames).Content);
        Assert.Equal(new[] { "typed-remove" }, peer.Labels);
        Assert.Null(TaxCollectorStore.OnMap(Map));
    }

    [Fact]
    public async Task Conquest_change_sends_world_information_before_a_detached_typed_actor_and_hides_the_actor_while_fighting()
    {
        var alliance = Alliance();
        var prism = PrismStore.Place(alliance.Id, 838, Map, 312, 5)!;
        using var own = new Recipient(Owner, Map);
        using var peer = new Recipient(Owner + 1, Map);
        using var fighter = new Recipient(Owner + 2, Map, fightId: 77);
        using var elsewhere = new Recipient(Owner + 3, Map + 1);
        const long next = 1_800_123_456_789;
        await ConquestHandler.ChangeStateAsync(prism, PrismStore.Pacified, next);
        Assert.Equal(new[] { "ifa", "typed-show" }, own.Labels);
        Assert.Equal(new[] { "ifa", "typed-show" }, peer.Labels);
        Assert.Equal(new[] { "ifa" }, fighter.Labels);
        Assert.Equal(new[] { "ifa" }, elsewhere.Labels);
        var actor = Show(Assert.Single(own.Codec.Frames));
        var state = Assert.IsType<RolePlayActorCasePrismActor>(Roleplay(actor).ActorCase).Value;
        Assert.Equal(PrismState.Protected, state.State);
        Assert.Equal(next, state.NextEvolutionDate);
        Assert.Equal(prism.PlacedUtcMs / 1000, state.PlacementDate);
        Assert.Null(state.CristalNumberLeft);
        Assert.Null(state.Cristal);
        var active = (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
            .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var fight = new FightInstance(7_966_002, Map) { DefenderLeaderId = prism.ContextId };
        Assert.True(active.TryAdd(fight.FightId, fight));
        try { await ConquestHandler.ChangeStateAsync(prism, PrismStore.Weakened, next + 90_000); }
        finally { active.TryRemove(fight.FightId, out _); }
        Assert.Equal(new[] { "ifa", "typed-show", "ifa" }, own.Labels);
        Assert.Single(own.Codec.Frames);
        Assert.Single(peer.Codec.Frames);
        Assert.Equal(PrismState.Protected, state.State);
        Assert.Equal(next, state.NextEvolutionDate);
    }

    private static AllianceStore.Alliance Alliance() => AllianceStore.Create(Owner, "Presence", "PRS", 104, 8, 12345, 97, 11862276);
    private static void SetMap(int area) => MapManager.Maps[Map] = new() { MapId = Map, SubAreaId = area, Outdoor = true };
    private static Task<byte[]> Read(ClientPipe pipe)
        => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
    private static ActorPositionInformation Show(GameFrame frame)
    {
        Assert.Null(frame.Uid);
        return Assert.Single(Assert.IsType<GameRolePlayShowActorsEvent>(frame.Content).Actors);
    }
    private static RolePlayActor Roleplay(ActorPositionInformation actor)
        => Assert.IsType<ActorInformationCaseRolePlayActor>(actor.ActorInformation!.ActorCase).Value;

    private static int PlacementTemplate(bool prism, int area)
    {
        using var db = new SqliteConnection(DatabaseManager.WorldConnectionString);
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT DISTINCT t.Id FROM ItemTemplates t
            JOIN json_each(t.Data, '$.possibleEffects.Array') p
            JOIN ItemEffects e ON e.Rid = json_extract(p.value, '$.rid')
            WHERE e.EffectId = $effect ORDER BY t.Id;
            """;
        query.Parameters.AddWithValue("$effect", prism ? PrismHandler.PlaceEffect : TaxCollectorHandler.PlaceEffect);
        using var rows = query.ExecuteReader();
        var candidates = new List<int>();
        while (rows.Read()) candidates.Add(rows.GetInt32(0));
        return candidates.First(id => prism
            ? PrismHandler.CriterionHolds(DatabaseManager.GetItemTemplateCriterion(id), Owner, area)
            : TaxCollectorHandler.CriterionHolds(DatabaseManager.GetItemTemplateCriterion(id), Owner, Interactives.LevelOfSubArea(area)));
    }

    private sealed class RecordingCodec : IGameCodec
    {
        internal List<GameFrame> Frames = [];
        internal bool Fail;
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (Fail) throw new IOException("recipient codec failed");
            return [0x71, checked((byte)Frames.Count)];
        }
        public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Unexpected presence decode");
    }

    private sealed class Recipient : IDisposable
    {
        internal List<byte[]> Packets = [];
        internal RecordingCodec Codec = new();
        internal GameSession Session;
        internal string[] Labels => Packets.Select(packet => packet is [0x71, var index]
            ? Codec.Frames[index - 1].Content is ContextRemoveElementEvent ? "typed-remove" : "typed-show"
            : ConnectionProtocol.ReadOpcode(packet)!).ToArray();
        internal Recipient(long id, long map, long fightId = 0, bool failTransport = false)
        {
            Session = new(Codec, packet =>
            {
                if (failTransport && packet is [0x71, _]) throw new IOException("recipient transport failed");
                Packets.Add(packet);
                return Task.CompletedTask;
            });
            Session.BindAccount(id, 1);
            Session.State.CharacterId = id;
            Session.State.MapId = map;
            Session.State.FightId = fightId;
            Session.EnterWorld();
            Assert.True(SessionRegistry.Register(Session));
        }
        public void Dispose() => SessionRegistry.Unregister(Session);
    }
}

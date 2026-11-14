using System.Reflection;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.World;

[Collection("MapManager")]
public sealed class TypedActorNotificationTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public async Task Standalone_npc_appearance_uses_the_explicit_owners_visibility_and_codec_without_broadcasting()
    {
        using var npc = new NpcFixture();
        var codec = new RecordingCodec();
        await using var own = await ClientPipe.OpenAsync(8_964_000_001, 9_964_000_001, npc.Spawn.MapId, codec: codec);
        await using var peer = await ClientPipe.OpenAsync(8_964_000_002, 9_964_000_002, npc.Spawn.MapId);
        own.Session.State.Quests = new QuestLog(new QuestCatalogue(), () => 200, () => own.Session.MapId);
        using (SessionContext.Push(peer.Session))
        using (await own.Session.EnterTurnAsync())
        {
            await NpcHandler.ComesInAsync(own.Session, npc.Spawn.NpcId);
            Assert.Same(peer.Session, SessionContext.Actual);
            // The same conditional placement is absent for the peer.
            await NpcHandler.ComesInAsync(peer.Session, npc.Spawn.NpcId);
        }
        Assert.Equal(new byte[] { 0x71 }, await Read(own));
        var actor = Assert.Single(Assert.IsType<GameRolePlayShowActorsEvent>(Assert.Single(codec.Frames).Content).Actors);
        Assert.Equal(npc.Spawn.ContextualId, actor.ActorId);
        Assert.Equal(new EntityDisposition(246, Direction.SouthWest, null, null), actor.Disposition);
        Assert.Equal(new[] { 200 }, actor.ActorInformation!.Look.Scales);
        Assert.Equal(5949, actor.ActorInformation.Look.Bones);
        var identity = Assert.IsType<RolePlayActorCaseNpcStaticInformation>(
            Assert.IsType<ActorInformationCaseRolePlayActor>(actor.ActorInformation.ActorCase).Value.ActorCase).Value;
        Assert.Equal(npc.Spawn.NpcId, identity.NpcId);
        Assert.Equal(Gender.Female, identity.Gender);
        Assert.Null(identity.Quests);
        Assert.Null(codec.Frames[0].Uid);
        Assert.False(peer.FromServer.DataAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scripted_hidden_npc_appears_before_dialogue_and_a_failed_appearance_stops_the_dialogue(bool fail)
    {
        using var npc = new NpcFixture();
        var codec = new RecordingCodec { Fail = fail };
        await using var own = await ClientPipe.OpenAsync(8_964_000_011, 9_964_000_011, npc.Spawn.MapId, codec: codec);
        await using var peer = await ClientPipe.OpenAsync(8_964_000_012, 9_964_000_012, npc.Spawn.MapId);
        using (SessionContext.Push(own.Session))
        using (await own.Session.EnterTurnAsync())
        {
            Assert.DoesNotContain(npc.Spawn, Npcs.OnMap(npc.Spawn.MapId));
            var sending = (Task)typeof(NpcHandler).GetMethod("ComesInAndSpeaksAsync", PrivateStatic)!
                .Invoke(null, [own.ToClient, npc.Spawn.NpcId, NpcFixture.Line])!;
            if (fail) await Assert.ThrowsAsync<IOException>(() => sending);
            else await sending;
        }
        var actor = Assert.Single(Assert.IsType<GameRolePlayShowActorsEvent>(Assert.Single(codec.Frames).Content).Actors);
        Assert.Equal(npc.Spawn.ContextualId, actor.ActorId);
        if (fail)
        {
            Assert.False(own.FromServer.DataAvailable);
            Assert.Equal(0, own.Session.State.OpenDialogueNpcId);
        }
        else
        {
            Assert.Equal(new byte[] { 0x71 }, await Read(own));
            Assert.Equal(ConnectionProtocol.BuildNpcDialog(npc.Spawn.MapId, npc.Spawn.ContextualId), await own.Next("ioc"));
            Assert.Equal(ConnectionProtocol.BuildNpcQuestion(NpcFixture.Line, []), await own.Next("ios"));
            Assert.Equal(npc.Spawn.NpcId, own.Session.State.OpenDialogueNpcId);
            Assert.Equal(NpcFixture.Line, own.Session.State.OpenDialogueMessage);
        }
        Assert.False(peer.FromServer.DataAvailable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Fight_end_sends_count_before_typed_replacement_only_to_roleplay_observers(int members)
    {
        const long map = 91_964_001, arena = 91_964_002, fightId = 7_964_001;
        var codec = new RecordingCodec();
        await using var observer = await ClientPipe.OpenAsync(8_964_000_021, 9_964_000_021, map, codec: codec);
        await using var fighter = await ClientPipe.OpenAsync(8_964_000_022, 9_964_000_022, map);
        await using var elsewhere = await ClientPipe.OpenAsync(8_964_000_023, 9_964_000_023, arena);
        fighter.Session.State.FightId = fightId + 1;
        var fight = new FightInstance(fightId, arena) { RoleplayMapId = map };
        var group = new MobSpawnManager.MobGroup
        {
            MobId = -9_964_001, CellId = 312, Orientation = 5,
            Members = Enumerable.Range(0, members).Select(i => new MobSpawnManager.MobMember
            {
                Monster = new() { Id = 81 + i, Look = $"{{{91 + i}|||115}}" }, Level = 99 - i,
            }).ToList(),
        };
        var ledger = typeof(FightHandler).GetField("_onTheMap", PrivateStatic)!.GetValue(null)!;
        var entryType = typeof(FightHandler).GetNestedType("OnTheMap", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, nonPublic: true)!;
        entryType.GetField("Replacement")!.SetValue(entry, group);
        Assert.True((bool)ledger.GetType().GetMethod("TryAdd")!.Invoke(ledger, [fightId, entry])!);
        using (SessionContext.Push(elsewhere.Session))
        using (await observer.Session.EnterTurnAsync())
            await ((Task)typeof(FightHandler).GetMethod("FightOffTheMapAsync", PrivateStatic)!.Invoke(null, [fight])!)
                .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FightJoinProtocol.BuildFightCount(0), await observer.Next("jqz"));
        if (members > 0)
        {
            Assert.Equal(new byte[] { 0x71 }, await Read(observer));
            var actor = Assert.Single(Assert.IsType<GameRolePlayShowActorsEvent>(Assert.Single(codec.Frames).Content).Actors);
            Assert.Equal(group.MobId, actor.ActorId);
            Assert.Equal(312, actor.Disposition.CellId);
            var identity = Assert.IsType<RolePlayActorCaseMonsterGroupActor>(
                Assert.IsType<ActorInformationCaseRolePlayActor>(actor.ActorInformation!.ActorCase).Value.ActorCase).Value;
            Assert.Equal(81, identity.Identification.MainCreature.Gid);
            Assert.Null(identity.Identification.MainCreature.Look);
            Assert.Equal(members - 1, identity.Identification.Underlings.Count);
        }
        else Assert.Empty(codec.Frames);
        Assert.False(observer.FromServer.DataAvailable);
        Assert.False(fighter.FromServer.DataAvailable);
        Assert.False(elsewhere.FromServer.DataAvailable);
    }

    private static Task<byte[]> Read(ClientPipe pipe)
        => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class RecordingCodec : IGameCodec
    {
        internal List<GameFrame> Frames = [];
        internal bool Fail;
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (Fail) throw new IOException("appearance failed");
            return [0x71];
        }
        public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Unexpected actor decode");
    }

    private sealed class NpcFixture : IDisposable
    {
        internal const long Line = 996_400_001;
        internal Npcs.Spawn Spawn = new()
        {
            MapId = 91_964_011, NpcId = 1_996_400_001, ContextualId = -20_004,
            Cell = 246, Orientation = 3, Bones = 5949, Scales = [200], Criterion = "PL>100",
        };
        private readonly Dictionary<long, List<Npcs.Spawn>> _maps;
        private readonly Dictionary<int, Npcs.Template> _templates;
        private readonly FieldInfo _dialoguesField = typeof(NpcDialogues).GetField("_dialogues", PrivateStatic)!;
        private readonly ContentStore<NpcDialogueKey, NpcDialogue> _original;

        internal NpcFixture()
        {
            _maps = (Dictionary<long, List<Npcs.Spawn>>)typeof(Npcs).GetProperty("_byMap", PrivateStatic)!.GetValue(null)!;
            _templates = (Dictionary<int, Npcs.Template>)typeof(Npcs).GetProperty("_templates", PrivateStatic)!.GetValue(null)!;
            _maps.Add(Spawn.MapId, [Spawn]);
            _templates.Add(Spawn.NpcId, new() { Id = Spawn.NpcId, Gender = 1 });
            NpcDialogues.Load();
            _original = (ContentStore<NpcDialogueKey, NpcDialogue>)_dialoguesField.GetValue(null)!;
            var dialogues = new ContentStore<NpcDialogueKey, NpcDialogue>();
            foreach (var row in _original.Rows) dialogues.Put(row.Key, row.Value.Value, row.Value.From);
            dialogues.Put(new(Spawn.NpcId, Spawn.MapId), new() { NpcId = Spawn.NpcId, MapId = Spawn.MapId, Lines = [new() { Message = Line }] },
                new Origin(ContentLayer.Authored, "typed-actor-test"));
            _dialoguesField.SetValue(null, dialogues);
        }

        public void Dispose()
        {
            _maps.Remove(Spawn.MapId);
            _templates.Remove(Spawn.NpcId);
            _dialoguesField.SetValue(null, _original);
        }
    }
}

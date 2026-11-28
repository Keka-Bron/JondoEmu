using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Quests;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests;

[Collection("MapManager")]
public sealed class QuestStateFlowTests
{
    private const long Character = 8_965_100_001, OtherCharacter = 8_965_100_002;
    /// <summary>
    /// A map of their own: they check that nothing else reached the owner, and 191105026 is where
    /// most tests stand, whose map broadcasts would.
    /// </summary>
    private const long Map = 990_100_184;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_journal_uses_the_explicit_owners_codec_and_tracker_order_with_empty_replacements(bool empty)
    {
        QuestEngine.Load();
        ClearFollowed(Character);
        ClearFollowed(OtherCharacter);
        var codec = new RecordingCodec();
        await using var own = await ClientPipe.OpenAsync(Character, Character, Map, codec: codec);
        await using var peer = await ClientPipe.OpenAsync(OtherCharacter, OtherCharacter, Map);
        var log = Log(own.Session);
        if (!empty)
        {
            log.Restore(1630, 2250, null, false).Done.Add(9680);
            log.Restore(1629, 2249, null, false).Done.Add(9655);
            log.Restore(2048, 0, null, true);
            log.Restore(99, int.MaxValue, null, false); // Missing catalogue step: skip and unfollow.
            foreach (int quest in new[] { 1629, 2048, 1630, 99 }) FollowedQuests.Follow(Character, quest);
        }
        FollowedQuests.Follow(OtherCharacter, 73);
        codec.AfterEncode = frame => { if (frame.Content is QuestsEvent && !empty) log.Run(1629)!.Done.Clear(); };
        try
        {
            using var context = SessionContext.Push(peer.Session);
            using var turn = await own.Session.EnterTurnAsync();
            await QuestEngine.SendJournalAsync(own.Session, box: true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(peer.Session, SessionContext.Actual);
            Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
            Assert.Equal(RecordingCodec.Token(1), await Read(own));
            Assert.Equal(RecordingCodec.Token(2), await Read(own));
            var journal = Assert.IsType<QuestsEvent>(codec.Frames[0].Content);
            var followed = Assert.IsType<QuestsFollowedEvent>(codec.Frames[1].Content);
            Assert.Equal(empty ? [] : new[] { 1630, 1629 }, journal.ActiveQuests.Select(quest => quest.QuestId));
            Assert.Equal(empty ? [] : new[] { 2048 }, journal.FinishedQuests.Select(quest => quest.QuestId));
            Assert.Equal(empty ? [] : new[] { 1629, 1630 }, followed.Quests.Select(quest => quest.QuestId));
            Assert.Equal(empty ? [] : new[] { 1629, 1630 }, FollowedQuests.Of(Character));
            Assert.Empty(journal.ReinitializedDoneQuestsId);
            if (!empty)
            {
                Assert.Same(journal.ActiveQuests[1], followed.Quests[0]);
                Assert.Equal(2249, followed.Quests[0].Details!.StepId);
                Assert.False(followed.Quests[0].Details!.Objectives.Single(objective => objective.ObjectiveId == 9655).ObjectivePending);
                Assert.True(followed.Quests[0].Details!.Objectives.Single(objective => objective.ObjectiveId == 9656).ObjectivePending);
                // 9680 is done and 9685 is reached; the two fights side by side can both be won,
                // and the return to the master waits for them.
                Assert.Equal(new[] { 9680, 9685, 9720, 10121 }, journal.ActiveQuests[0].Details!.Objectives.Select(objective => objective.ObjectiveId));
            }
            Assert.All(codec.Frames, frame => Assert.Null(frame.Uid));
            Assert.Equal(0, codec.Decodes);
            Assert.False(own.FromServer.DataAvailable);
            Assert.False(peer.FromServer.DataAvailable);
            Assert.Equal(new[] { 73 }, FollowedQuests.Of(OtherCharacter));
        }
        finally { ClearFollowed(Character); ClearFollowed(OtherCharacter); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Failed_live_journal_or_tracker_encoding_preserves_the_existing_cleanup_boundary(int failAt)
    {
        QuestEngine.Load();
        ClearFollowed(Character);
        var codec = new RecordingCodec { FailAt = failAt };
        await using var own = await ClientPipe.OpenAsync(Character, Character, Map, codec: codec);
        Log(own.Session).Restore(2048, 0, null, true);
        FollowedQuests.Follow(Character, 2048);
        var wrong = GameSession.SinSocket();
        try
        {
            using var context = SessionContext.Push(wrong);
            using var turn = await own.Session.EnterTurnAsync();
            Assert.Same(codec.Failure, await Assert.ThrowsAsync<IOException>(() => QuestEngine.SendJournalAsync(own.Session, box: true)));
            if (failAt == 1) Assert.Equal(RecordingCodec.Token(1), await Read(own));
            Assert.Equal(failAt == 0 ? new[] { 2048 } : [], FollowedQuests.Of(Character));
            Assert.True(own.Session.State.Quests!.Finished(2048));
            Assert.Equal(failAt + 1, codec.Frames.Count);
            Assert.False(own.FromServer.DataAvailable);
            Assert.Same(wrong, SessionContext.Actual);
        }
        finally { ClearFollowed(Character); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_marks_use_owner_quest_progress_contextual_ids_and_fresh_replacements_on_repeated_loads(bool fail)
    {
        const long mapId = 188743684;
        QuestEngine.Load();
        Npcs.Initialize();
        var codec = new RecordingCodec { FailAt = fail ? 0 : -1 };
        await using var own = await ClientPipe.OpenAsync(Character, Character, mapId, codec: codec);
        var step = QuestEngine.Book!.Of(1994)!.Steps.First(value => value.Objectives.Any(objective => objective.Id == 13793));
        var run = Log(own.Session).Restore(1994, step.Id, null, false);
        var wrong = GameSession.SinSocket();
        using var context = SessionContext.Push(wrong);
        using var turn = await own.Session.EnterTurnAsync();

        if (fail) Assert.Same(codec.Failure, await Assert.ThrowsAsync<IOException>(() => QuestEngine.SendMarksAsync(own.Session, mapId)));
        else
        {
            await QuestEngine.SendMarksAsync(own.Session, mapId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RecordingCodec.Token(1), await Read(own));
        }
        var first = Assert.Single(Assert.IsType<NpcsMapQuestStatusUpdateEvent>(codec.Frames[0].Content).MapInformation);
        Assert.Equal(mapId, first.MapId);
        Npcs.Spawn recruiter;
        using (SessionContext.Push(own.Session)) recruiter = Assert.Single(Npcs.OnMap(mapId), npc => npc.NpcId == 735);
        Assert.Contains(first.NpcsWithQuests, npc => npc.NpcId == recruiter.ContextualId && npc.QuestsToValidate.Contains(1994));
        if (!fail)
        {
            run.Done.Add(13793);
            await QuestEngine.SendMarksAsync(own.Session, mapId);
            Assert.Equal(RecordingCodec.Token(2), await Read(own));
            var updated = Assert.IsType<NpcsMapQuestStatusUpdateEvent>(codec.Frames[1].Content);
            Assert.DoesNotContain(updated.MapInformation.SelectMany(map => map.NpcsWithQuests), npc => npc.NpcId == recruiter.ContextualId && npc.QuestsToValidate.Contains(1994));
            own.Session.State.MapId = 91_965_999;
            await QuestEngine.SendMarksAsync(own.Session, own.Session.MapId);
            Assert.Equal(RecordingCodec.Token(3), await Read(own));
            Assert.Empty(Assert.IsType<NpcsMapQuestStatusUpdateEvent>(codec.Frames[2].Content).MapInformation);
        }
        Assert.False(own.FromServer.DataAvailable);
        Assert.Same(wrong, SessionContext.Actual);
        Assert.Equal(0, own.Session.UnoCadaVez.CurrentCount);
        Assert.Equal(0, codec.Decodes);
        Assert.All(codec.Frames, frame => Assert.Null(frame.Uid));
    }

    [Fact]
    public async Task Uninitialized_owner_and_zero_map_do_not_send_or_touch_another_sessions_state()
    {
        QuestEngine.Load();
        var codec = new RecordingCodec();
        var delivered = new List<byte[]>();
        var own = new GameSession(codec, packet => { delivered.Add(packet); return Task.CompletedTask; });
        var wrong = GameSession.SinSocket();
        Log(wrong).Restore(1629, 2249, null, false);
        using var context = SessionContext.Push(wrong);
        await QuestEngine.SendJournalAsync(own, box: true);
        await QuestEngine.SendMarksAsync(own, 191105026);
        Log(own);
        await QuestEngine.SendMarksAsync(own, 0);
        Assert.Empty(codec.Frames);
        Assert.Empty(delivered);
        Assert.Same(wrong, SessionContext.Actual);
    }

    private static QuestLog Log(GameSession session)
    {
        var log = new QuestLog(QuestEngine.Book!, () => session.State.CharacterLevel, () => session.MapId);
        session.State.CharacterLevel = 200;
        session.State.Quests = log;
        return log;
    }
    private static void ClearFollowed(long characterId)
    {
        foreach (int questId in FollowedQuests.Of(characterId)) FollowedQuests.Unfollow(characterId, questId);
    }
    private static Task<byte[]> Read(ClientPipe pipe)
        => Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class RecordingCodec : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal readonly IOException Failure = new("quest encoding failed");
        internal int FailAt = -1, Decodes;
        internal Action<GameFrame>? AfterEncode;
        internal static byte[] Token(int index) => [0xC7, 0x66, checked((byte)index)];
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (Frames.Count - 1 == FailAt) throw Failure;
            AfterEncode?.Invoke(frame);
            return Token(Frames.Count);
        }
        public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new InvalidOperationException("Quest sends must not decode intermediate packets."); }
    }
}

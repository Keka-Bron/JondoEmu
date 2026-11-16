using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class QuestStateTests
{
    [Fact]
    public void Journal_snapshots_detach_objective_states_finished_ids_and_declaration_order()
    {
        var objectives = new List<int> { 91_003, 91_001, 91_002 };
        var done = new HashSet<int> { 91_001 };
        var active = new List<QuestActive> { QuestMessages.Active(81_002, 82_003, objectives, done) };
        var finished = new List<int> { 81_005, 81_004 };
        var snapshot = QuestMessages.Journal(active, finished);
        objectives.Clear();
        done.Clear();
        active.Clear();
        finished.Clear();

        var quest = Assert.Single(snapshot.ActiveQuests);
        Assert.Equal(81_002, quest.QuestId);
        Assert.Equal(82_003, quest.Details!.StepId);
        Assert.Equal(new[] { 91_003, 91_001, 91_002 }, quest.Details.Objectives.Select(objective => objective.ObjectiveId));
        Assert.Equal(new[] { true, false, true }, quest.Details.Objectives.Select(objective => objective.ObjectivePending));
        Assert.All(quest.Details.Objectives, objective => { Assert.Null(objective.Completion); Assert.Empty(objective.DialogParams); });
        Assert.Equal(new[] { 81_005, 81_004 }, snapshot.FinishedQuests.Select(value => value.QuestId));
        Assert.Empty(snapshot.ReinitializedDoneQuestsId);
    }

    [Fact]
    public async Task Journal_precedes_followed_and_cleanup_with_the_same_detached_entries_and_explicit_owner()
    {
        var backend = new Backend();
        backend.Tracker.AddRange([81_001, 81_004, 81_002, 81_009]);
        var target = new Recipient();
        var ambient = new Recipient(71_002);
        target.AfterSend = frame =>
        {
            backend.Calls.Add(frame.Content is QuestsEvent ? "send-journal" : "send-followed");
            if (frame.Content is QuestsEvent) { backend.Objectives.Clear(); backend.Done.Clear(); }
        };
        using var context = SessionContext.Push(ambient.Session);
        using var turn = await target.Session.EnterTurnAsync();

        await new QuestStateService(backend).SendJournalAsync(target.Session, box: true).WaitAsync(TimeSpan.FromSeconds(5));

        var journal = Assert.IsType<QuestsEvent>(target.Codec.Frames[0].Content);
        var followed = Assert.IsType<QuestsFollowedEvent>(target.Codec.Frames[1].Content);
        Assert.Equal(new[] { 81_002, 81_001 }, journal.ActiveQuests.Select(quest => quest.QuestId));
        Assert.Equal(new[] { 81_001, 81_002 }, followed.Quests.Select(quest => quest.QuestId));
        Assert.Same(journal.ActiveQuests[1], followed.Quests[0]);
        Assert.Same(journal.ActiveQuests[0], followed.Quests[1]);
        Assert.Equal(new[] { false, true }, followed.Quests[1].Details!.Objectives.Select(objective => objective.ObjectivePending));
        Assert.Equal(new[] { "journal:71001", "send-journal", "followed:71001", "unfollow:71001:81004", "unfollow:71001:81009", "send-followed" }, backend.Calls);
        Assert.Equal(new[] { 81_001, 81_002 }, backend.Tracker);
        Assert.Equal(2, target.Delivered.Count);
        Assert.All(target.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(0, target.Codec.Decodes);
        Assert.Equal(0, target.Session.UnoCadaVez.CurrentCount);
        Assert.Same(ambient.Session, SessionContext.Actual);
        Assert.Empty(ambient.Codec.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_loaded_journals_still_send_and_only_entry_sends_an_empty_tracker(bool box)
    {
        var backend = new Backend { Empty = true };
        var target = new Recipient();
        await new QuestStateService(backend).SendJournalAsync(target.Session, box);
        var journal = Assert.IsType<QuestsEvent>(target.Codec.Frames[0].Content);
        Assert.Empty(journal.ActiveQuests);
        Assert.Empty(journal.FinishedQuests);
        Assert.Equal(box ? 2 : 1, target.Codec.Frames.Count);
        if (box) Assert.Empty(Assert.IsType<QuestsFollowedEvent>(target.Codec.Frames[1].Content).Quests);
        else Assert.DoesNotContain(backend.Calls, call => call.StartsWith("followed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_log_or_catalogue_is_omission_and_does_not_clean_the_tracker()
    {
        var backend = new Backend { Available = false };
        backend.Tracker.Add(81_004);
        var target = new Recipient();
        var service = new QuestStateService(backend);
        await service.SendJournalAsync(target.Session, box: true);
        await service.SendMarksAsync(target.Session, 83_001);
        Assert.Empty(target.Codec.Frames);
        Assert.Equal(new[] { 81_004 }, backend.Tracker);
        Assert.Equal(new[] { "journal:71001", "marks:71001:83001" }, backend.Calls);
    }

    [Fact]
    public async Task Ordinary_journal_refresh_never_reads_or_cleans_followed_state()
    {
        var backend = new Backend();
        backend.Tracker.Add(81_009);
        var target = new Recipient();
        await new QuestStateService(backend).SendJournalAsync(target.Session);
        Assert.IsType<QuestsEvent>(Assert.Single(target.Codec.Frames).Content);
        Assert.Equal(new[] { "journal:71001" }, backend.Calls);
        Assert.Equal(new[] { 81_009 }, backend.Tracker);
    }

    [Theory]
    [InlineData("journal:71001", 0)]
    [InlineData("followed:71001", 1)]
    [InlineData("unfollow:71001:81009", 1)]
    [InlineData("marks:71001:83001", 0)]
    public async Task Source_and_cleanup_failures_stop_before_later_sends_without_retry(string stage, int sent)
    {
        var backend = new Backend { FailAt = stage };
        backend.Tracker.AddRange([81_001, 81_009]);
        var target = new Recipient();
        var service = new QuestStateService(backend);
        Assert.Same(backend.Failure, await Assert.ThrowsAsync<IOException>(() => stage.StartsWith("marks:", StringComparison.Ordinal)
            ? service.SendMarksAsync(target.Session, 83_001) : service.SendJournalAsync(target.Session, box: true)));
        Assert.Equal(sent, target.Codec.Frames.Count);
        Assert.Equal(sent, target.Delivered.Count);
        Assert.Equal(1, backend.Calls.Count(call => call == stage));
        Assert.Equal(new[] { 81_001, 81_009 }, backend.Tracker);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task Delivery_failure_retains_cleanup_only_after_a_successful_journal_and_never_retries(int index, bool codecFailure)
    {
        var backend = new Backend();
        backend.Tracker.AddRange([81_001, 81_009]);
        var failure = new IOException("quest delivery failed");
        var target = new Recipient { FailAt = index, CodecFailure = codecFailure, Failure = failure };

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new QuestStateService(backend).SendJournalAsync(target.Session, box: true)));
        Assert.Equal(index + 1, target.Codec.Frames.Count);
        Assert.Equal(index, target.Delivered.Count);
        Assert.Equal(codecFailure ? index : index + 1, target.Sends);
        Assert.Equal(index == 0 ? new[] { 81_001, 81_009 } : new[] { 81_001 }, backend.Tracker);
        Assert.Equal(index == 0 ? new[] { "journal:71001" } : new[] { "journal:71001", "followed:71001", "unfollow:71001:81009" }, backend.Calls);
        Assert.Equal(0, target.Codec.Decodes);
    }

    [Fact]
    public void Marks_filter_empty_actors_and_detach_both_lists_while_retaining_contextual_ids_and_large_maps()
    {
        var offers = new List<int> { 81_003, 81_001 };
        var waiting = new List<int> { 81_002, 81_004 };
        var rows = new List<(long, IReadOnlyList<int>, IReadOnlyList<int>)> { (-20_001, [], []), (-20_000, offers, waiting), (int.MaxValue, [], [81_005]) };
        var marks = QuestMessages.Marks(long.MaxValue, rows);
        offers.Clear();
        waiting.Clear();
        rows.Clear();
        var map = Assert.Single(marks.MapInformation);
        Assert.Equal(long.MaxValue, map.MapId);
        Assert.Equal(new[] { -20_000, int.MaxValue }, map.NpcsWithQuests.Select(npc => npc.NpcId));
        Assert.Equal(new[] { 81_003, 81_001 }, map.NpcsWithQuests[0].QuestsToStart);
        Assert.Equal(new[] { 81_002, 81_004 }, map.NpcsWithQuests[0].QuestsToValidate);
        Assert.Equal(new[] { 81_005 }, map.NpcsWithQuests[1].QuestsToValidate);
        Assert.Empty(QuestMessages.Marks(83_001, [(-20_000L, [], [])]).MapInformation);
        Assert.Empty(QuestMessages.Marks(83_001, []).MapInformation);
    }

    [Theory]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData((long)int.MinValue - 1)]
    public void Mark_actor_ids_fail_instead_of_narrowing(long actor)
        => Assert.Throws<OverflowException>(() => QuestMessages.Marks(83_001, [(actor, [81_001], [])]));

    [Fact]
    public async Task Repeated_marks_read_the_requested_map_and_send_a_fresh_empty_replacement()
    {
        var backend = new Backend();
        var target = new Recipient();
        var ambient = new Recipient(71_002);
        using var context = SessionContext.Push(ambient.Session);
        using var turn = await target.Session.EnterTurnAsync();
        var service = new QuestStateService(backend);
        await service.SendMarksAsync(target.Session, 8_000_000_003);
        backend.Empty = true;
        await service.SendMarksAsync(target.Session, 83_002);
        Assert.Equal(8_000_000_003, Assert.Single(Assert.IsType<NpcsMapQuestStatusUpdateEvent>(target.Codec.Frames[0].Content).MapInformation).MapId);
        Assert.Empty(Assert.IsType<NpcsMapQuestStatusUpdateEvent>(target.Codec.Frames[1].Content).MapInformation);
        Assert.Equal(new[] { "marks:71001:8000000003", "marks:71001:83002" }, backend.Calls);
        Assert.All(target.Codec.Frames, frame => Assert.Null(frame.Uid));
        Assert.Equal(2, target.Delivered.Count);
        Assert.Same(ambient.Session, SessionContext.Actual);
        Assert.Empty(ambient.Codec.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_mark_delivery_does_not_retry_or_send_a_raw_replacement(bool codecFailure)
    {
        var backend = new Backend();
        var target = new Recipient { FailAt = 0, CodecFailure = codecFailure };
        Assert.Same(target.Failure, await Assert.ThrowsAsync<IOException>(() => new QuestStateService(backend).SendMarksAsync(target.Session, 83_001)));
        Assert.Single(target.Codec.Frames);
        Assert.Empty(target.Delivered);
        Assert.Equal(codecFailure ? 0 : 1, target.Sends);
        Assert.Single(backend.Calls);
    }

    private sealed class Backend : IQuestStateBackend
    {
        internal readonly List<string> Calls = [];
        internal readonly List<int> Tracker = [];
        internal readonly List<int> Objectives = [91_003, 91_001];
        internal readonly HashSet<int> Done = [91_003];
        internal bool Empty, Available = true;
        internal string FailAt = "";
        internal readonly IOException Failure = new("quest source failed");
        private void Call(string stage) { Calls.Add(stage); if (FailAt == stage) throw Failure; }
        public QuestsEvent? Journal(GameSession session)
        {
            Call($"journal:{session.CharacterId}");
            return !Available ? null : QuestMessages.Journal(Empty ? [] : [QuestMessages.Active(81_002, 82_003, Objectives, Done), QuestMessages.Active(81_001, 82_001, [], [])], Empty ? [] : [81_004]);
        }
        public IReadOnlyList<int> Followed(long characterId) { Call($"followed:{characterId}"); return Tracker; }
        public void Unfollow(long characterId, int questId) { Call($"unfollow:{characterId}:{questId}"); Tracker.Remove(questId); }
        public NpcsMapQuestStatusUpdateEvent? Marks(GameSession session, long mapId)
        {
            Call($"marks:{session.CharacterId}:{mapId}");
            return !Available ? null : QuestMessages.Marks(mapId, Empty ? [] : [(-20_000L, [81_001], [81_002])]);
        }
    }

    private sealed class Recipient
    {
        internal readonly RecordingCodec Codec;
        internal readonly GameSession Session;
        internal readonly List<byte[]> Delivered = [];
        internal int Sends, FailAt = -1;
        internal bool CodecFailure;
        internal IOException Failure = new("quest transport failed");
        internal Action<GameFrame>? AfterSend;
        internal Recipient(long characterId = 71_001)
        {
            Codec = new((frame, index) => { if (CodecFailure && index == FailAt) throw Failure; });
            Session = new(Codec, packet =>
            {
                if (Sends++ == FailAt && !CodecFailure) throw Failure;
                Delivered.Add(packet);
                AfterSend?.Invoke(Codec.Frames[^1]);
                return Task.CompletedTask;
            });
            Session.State.CharacterId = characterId;
        }
    }

    private sealed class RecordingCodec(Action<GameFrame, int> onEncode) : IGameCodec
    {
        internal readonly List<GameFrame> Frames = [];
        internal int Decodes;
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); onEncode(frame, Frames.Count - 1); return [0xC7, 0x65, checked((byte)Frames.Count)]; }
        public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new InvalidOperationException("Quest sends must not decode intermediate packets."); }
    }
}

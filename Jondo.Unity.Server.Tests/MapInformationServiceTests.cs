using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class MapInformationServiceTests
{
    private static readonly string[] MapSequence =
    [
        "forget", "dream", "prepare-anomaly", "marks", "actors", "fight-count", "is-bag",
        "modifier", "raid", "tower", "season", "anomaly", "gargandias", "guild-hall",
        "subarea", "quest", "achievement", "conquest", "lingering-room", "phantom", "staging", "pending-bonus",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Map_reply_preserves_all_hooks_and_furniture_placement_under_the_owners_existing_turn(bool bag)
    {
        var h = new Harness { IsBag = bag };
        var ambient = new Harness();
        using (SessionContext.Push(ambient.Session))
        using (await h.Session.EnterTurnAsync())
        {
            await h.Service.SendAsync(h.Session, h.Character).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(ambient.Session, SessionContext.Actual);
            Assert.Equal(0, h.Session.UnoCadaVez.CurrentCount);
        }

        var expected = MapSequence.ToList();
        if (bag) expected.Insert(expected.IndexOf("modifier"), "furniture");
        Assert.Equal(expected, h.Calls);
        var frame = Assert.Single(h.Codec.Frames);
        Assert.Empty(Assert.IsType<AreaFightModifierUpdateEvent>(frame.Content).SpellPairs);
        Assert.Null(frame.Uid);
        Assert.Equal(0, h.Codec.Decodes);
        Assert.Empty(ambient.Calls);
        Assert.Empty(ambient.Codec.Frames);
    }

    [Fact]
    public async Task Actor_refresh_sends_only_contents_and_modifier_without_replaying_map_arrival_hooks()
    {
        var h = new Harness { IsBag = true };
        var ambient = new Harness();
        using (SessionContext.Push(ambient.Session))
        using (await h.Session.EnterTurnAsync())
        {
            await h.Service.RefreshActorsAsync(h.Session, h.Character).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(ambient.Session, SessionContext.Actual);
        }
        Assert.Equal(new[] { "actors", "modifier" }, h.Calls);
        Assert.IsType<AreaFightModifierUpdateEvent>(Assert.Single(h.Codec.Frames).Content);
        Assert.Empty(ambient.Calls);
    }

    [Fact]
    public async Task Repeated_replies_read_current_owner_state_without_repeating_world_initialization()
    {
        var h = new Harness();
        await h.Service.SendAsync(h.Session, h.Character);
        h.Session.State.MapId++;
        h.Session.State.CellId = 312;
        h.Session.State.Orientation = 7;
        h.IsBag = true;
        await h.Service.SendAsync(h.Session, h.Character);

        Assert.Equal(new[]
        {
            (9_000_000_001L, 145, 3),
            (9_000_000_002L, 312, 7),
        }, h.ActorSources);
        Assert.Equal(2, h.Calls.Count(call => call == "marks"));
        Assert.Equal(2, h.Calls.Count(call => call == "quest"));
        Assert.Equal(2, h.Calls.Count(call => call == "modifier"));
        Assert.Single(h.Calls, call => call == "furniture");
    }

    [Fact]
    public async Task Shared_service_keeps_two_owners_isolated_across_an_await()
    {
        var first = new Harness();
        var second = new Harness();
        second.Session.State.CharacterId++;
        second.Character.Id++;
        second.Session.State.MapId++;
        second.Session.State.CellId = 219;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.BeforeActors = async () =>
        {
            waiting.SetResult();
            await resume.Task;
            Assert.Same(first.Session, SessionContext.Actual);
        };
        var backend = new RoutingBackend(first, second);
        var service = new MapInformationService(backend);
        Task sending = service.SendAsync(first.Session, first.Character);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.SendAsync(second.Session, second.Character);
            Assert.Single(second.ActorSources);
            Assert.Empty(first.ActorSources);
        }
        finally { resume.TrySetResult(); }
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { (9_000_000_001L, 145, 3) }, first.ActorSources);
        Assert.Equal(new[] { (9_000_000_002L, 219, 3) }, second.ActorSources);
        Assert.Single(first.Codec.Frames);
        Assert.Single(second.Codec.Frames);
    }

    public static IEnumerable<object[]> HookFailures()
        => MapSequence.Where(stage => stage != "modifier").Append("furniture").Select(stage => new object[] { stage });

    [Theory]
    [MemberData(nameof(HookFailures))]
    public async Task Hook_failures_propagate_and_stop_later_work_without_retry(string stage)
    {
        var h = new Harness { IsBag = true, FailAt = stage };
        var ambient = new Harness();
        using (SessionContext.Push(ambient.Session))
        {
            Assert.Same(h.Failure, await Assert.ThrowsAsync<IOException>(() => h.Service.SendAsync(h.Session, h.Character)));
            Assert.Same(ambient.Session, SessionContext.Actual);
        }

        var expected = MapSequence.ToList();
        expected.Insert(expected.IndexOf("modifier"), "furniture");
        Assert.Equal(expected.Take(expected.IndexOf(stage) + 1), h.Calls);
        Assert.Equal(expected.IndexOf(stage) > expected.IndexOf("modifier") ? 1 : 0, h.Codec.Frames.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Modifier_codec_and_transport_failures_stop_before_post_load_hooks(bool refresh, bool transport)
    {
        var h = new Harness { FailCodec = !transport, FailTransport = transport };
        var ambient = new Harness();
        using (SessionContext.Push(ambient.Session))
        {
            Assert.Same(h.Failure, await Assert.ThrowsAsync<IOException>(() => refresh
                ? h.Service.RefreshActorsAsync(h.Session, h.Character)
                : h.Service.SendAsync(h.Session, h.Character)));
            Assert.Same(ambient.Session, SessionContext.Actual);
        }
        Assert.Equal(refresh ? new[] { "actors", "modifier" } : MapSequence.Take(8), h.Calls);
        Assert.Single(h.Codec.Frames);
        Assert.Equal(transport ? 1 : 0, h.Sends);
        Assert.Equal(0, h.Codec.Decodes);
    }

    [Fact]
    public async Task Actor_refresh_failure_does_not_send_the_modifier()
    {
        var h = new Harness { FailAt = "actors" };
        Assert.Same(h.Failure, await Assert.ThrowsAsync<IOException>(() => h.Service.RefreshActorsAsync(h.Session, h.Character)));
        Assert.Equal(new[] { "actors" }, h.Calls);
        Assert.Empty(h.Codec.Frames);
    }

    [Fact]
    public async Task Typed_quest_marks_precede_actors_on_each_repeatable_map_reply()
    {
        var h = new Harness { QuestState = new QuestStateService(new QuestBackend()) };
        await h.Service.SendAsync(h.Session, h.Character);
        h.Session.State.MapId++;
        await h.Service.SendAsync(h.Session, h.Character);

        var expected = MapSequence.ToList();
        expected.Insert(expected.IndexOf("actors"), "marks-event");
        Assert.Equal(expected.Concat(expected), h.Calls);
        Assert.Equal(4, h.Codec.Frames.Count);
        Assert.Equal(9_000_000_001, Assert.Single(Assert.IsType<NpcsMapQuestStatusUpdateEvent>(h.Codec.Frames[0].Content).MapInformation).MapId);
        Assert.IsType<AreaFightModifierUpdateEvent>(h.Codec.Frames[1].Content);
        Assert.Equal(9_000_000_002, Assert.Single(Assert.IsType<NpcsMapQuestStatusUpdateEvent>(h.Codec.Frames[2].Content).MapInformation).MapId);
        Assert.IsType<AreaFightModifierUpdateEvent>(h.Codec.Frames[3].Content);
        Assert.Equal(0, h.Codec.Decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_mark_delivery_failure_stops_before_map_actors_and_arrival_processing(bool transport)
    {
        var h = new Harness { QuestState = new QuestStateService(new QuestBackend()), FailCodec = !transport, FailTransport = transport };
        Assert.Same(h.Failure, await Assert.ThrowsAsync<IOException>(() => h.Service.SendAsync(h.Session, h.Character)));
        Assert.Equal(new[] { "forget", "dream", "prepare-anomaly", "marks", "marks-event" }, h.Calls);
        Assert.Empty(h.ActorSources);
        Assert.Single(h.Codec.Frames);
        Assert.Equal(transport ? 1 : 0, h.Sends);
    }

    private sealed class QuestBackend : IQuestStateBackend
    {
        public QuestsEvent? Journal(GameSession session) => throw new InvalidOperationException("Map replies do not resend the journal.");
        public IReadOnlyList<int> Followed(long characterId) => throw new InvalidOperationException("Map replies do not read the tracker.");
        public void Unfollow(long characterId, int questId) => throw new InvalidOperationException("Map replies do not clean the tracker.");
        public NpcsMapQuestStatusUpdateEvent? Marks(GameSession session, long mapId)
        {
            Assert.Equal(session.MapId, mapId);
            return QuestMessages.Marks(mapId, [(-20_000L, [81_001], [81_002])]);
        }
    }

    private sealed class Harness : IMapInformationBackend
    {
        internal List<string> Calls { get; } = [];
        internal List<(long Map, int Cell, int Orientation)> ActorSources { get; } = [];
        internal GameSession Session { get; }
        internal DbCharacter Character { get; } = new() { Id = 71_001, Name = "Map visitor" };
        internal RecordingCodec Codec { get; }
        internal MapInformationService Service { get; }
        internal bool IsBag, FailCodec, FailTransport;
        internal int Sends;
        internal string? FailAt;
        internal Func<Task>? BeforeActors;
        internal QuestStateService? QuestState;
        internal IOException Failure { get; } = new("map operation failed");

        internal Harness()
        {
            Codec = new(frame =>
            {
                if (frame.Content is NpcsMapQuestStatusUpdateEvent)
                {
                    Assert.NotNull(QuestState);
                    Calls.Add("marks-event");
                    if (FailCodec) throw Failure;
                    return;
                }
                Assert.IsType<AreaFightModifierUpdateEvent>(frame.Content);
                Calls.Add("modifier");
                if (FailCodec) throw Failure;
            });
            Session = new(Codec, _ =>
            {
                Sends++;
                if (FailTransport) throw Failure;
                return Task.CompletedTask;
            });
            Session.State.CharacterId = Character.Id;
            Session.State.MapId = 9_000_000_001;
            Session.State.CellId = 145;
            Session.State.Orientation = 3;
            Service = new(this);
        }

        private void Record(string call, GameSession session)
        {
            Assert.Same(Session, session);
            Assert.Same(Session, SessionContext.Actual);
            Calls.Add(call);
            if (FailAt == call) throw Failure;
        }

        private Task Hook(string call, GameSession session) { Record(call, session); return Task.CompletedTask; }
        public void ForgetInteractions(GameSession session) => Record("forget", session);
        public Task RestoreDreamAsync(GameSession session) => Hook("dream", session);
        public void PrepareAnomaly(long mapId) { Assert.Equal(Session.MapId, mapId); Record("prepare-anomaly", Session); }
        public async Task SendMarksAsync(GameSession session)
        {
            await Hook("marks", session);
            if (QuestState != null) await QuestState.SendMarksAsync(session, session.MapId);
        }
        public async Task SendActorsAsync(GameSession session, DbCharacter character)
        {
            Record("actors", session);
            Assert.Same(Character, character);
            Assert.Equal(session.CharacterId, character.Id);
            if (BeforeActors != null) await BeforeActors();
            ActorSources.Add((session.MapId, session.State.CellId, session.State.Orientation));
        }
        public Task SendFightCountAsync(GameSession session) => Hook("fight-count", session);
        public bool IsHavenBag(long mapId) { Assert.Equal(Session.MapId, mapId); Record("is-bag", Session); return IsBag; }
        public Task SendFurnitureAsync(GameSession session) => Hook("furniture", session);
        public Task RestoreRaidStagingsAsync(GameSession session) => Hook("raid", session);
        public Task RestoreTravelersTowerAsync(GameSession session) => Hook("tower", session);
        public Task RestoreSeasonAsync(GameSession session) => Hook("season", session);
        public Task RestoreAnomalyAsync(GameSession session) => Hook("anomaly", session);
        public Task RestoreGargandiasAsync(GameSession session) => Hook("gargandias", session);
        public Task RestoreGuildHallAsync(GameSession session) => Hook("guild-hall", session);
        public int SubAreaOf(long mapId) { Assert.Equal(Session.MapId, mapId); Record("subarea", Session); return 83_002; }
        public Task EnterQuestMapAsync(GameSession session, int subAreaId) { Assert.Equal(83_002, subAreaId); return Hook("quest", session); }
        public Task EnterAchievementMapAsync(GameSession session, int subAreaId) { Assert.Equal(83_002, subAreaId); return Hook("achievement", session); }
        public Task EnterConquestMapAsync(GameSession session, int subAreaId) { Assert.Equal(83_002, subAreaId); return Hook("conquest", session); }
        public Task EnterLingeringRoomAsync(GameSession session) => Hook("lingering-room", session);
        public Task RestorePhantomAsync(GameSession session) => Hook("phantom", session);
        public Task ShowMapStagingAsync(GameSession session) => Hook("staging", session);
        public Task PlacePendingBonusItemsAsync(GameSession session) => Hook("pending-bonus", session);
    }

    private sealed class RoutingBackend(Harness first, Harness second) : IMapInformationBackend
    {
        private Harness Owner(GameSession session) => session == first.Session ? first : second;
        private Harness Current => Owner(SessionContext.Actual!);
        public void ForgetInteractions(GameSession session) => Owner(session).ForgetInteractions(session);
        public Task RestoreDreamAsync(GameSession session) => Owner(session).RestoreDreamAsync(session);
        public void PrepareAnomaly(long mapId) => Current.PrepareAnomaly(mapId);
        public Task SendMarksAsync(GameSession session) => Owner(session).SendMarksAsync(session);
        public Task SendActorsAsync(GameSession session, DbCharacter character) => Owner(session).SendActorsAsync(session, character);
        public Task SendFightCountAsync(GameSession session) => Owner(session).SendFightCountAsync(session);
        public bool IsHavenBag(long mapId) => Current.IsHavenBag(mapId);
        public Task SendFurnitureAsync(GameSession session) => Owner(session).SendFurnitureAsync(session);
        public Task RestoreRaidStagingsAsync(GameSession session) => Owner(session).RestoreRaidStagingsAsync(session);
        public Task RestoreTravelersTowerAsync(GameSession session) => Owner(session).RestoreTravelersTowerAsync(session);
        public Task RestoreSeasonAsync(GameSession session) => Owner(session).RestoreSeasonAsync(session);
        public Task RestoreAnomalyAsync(GameSession session) => Owner(session).RestoreAnomalyAsync(session);
        public Task RestoreGargandiasAsync(GameSession session) => Owner(session).RestoreGargandiasAsync(session);
        public Task RestoreGuildHallAsync(GameSession session) => Owner(session).RestoreGuildHallAsync(session);
        public int SubAreaOf(long mapId) => Current.SubAreaOf(mapId);
        public Task EnterQuestMapAsync(GameSession session, int subAreaId) => Owner(session).EnterQuestMapAsync(session, subAreaId);
        public Task EnterAchievementMapAsync(GameSession session, int subAreaId) => Owner(session).EnterAchievementMapAsync(session, subAreaId);
        public Task EnterConquestMapAsync(GameSession session, int subAreaId) => Owner(session).EnterConquestMapAsync(session, subAreaId);
        public Task EnterLingeringRoomAsync(GameSession session) => Owner(session).EnterLingeringRoomAsync(session);
        public Task RestorePhantomAsync(GameSession session) => Owner(session).RestorePhantomAsync(session);
        public Task ShowMapStagingAsync(GameSession session) => Owner(session).ShowMapStagingAsync(session);
        public Task PlacePendingBonusItemsAsync(GameSession session) => Owner(session).PlacePendingBonusItemsAsync(session);
    }

    private sealed class RecordingCodec(Action<GameFrame> onEncode) : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        internal int Decodes { get; private set; }
        public byte[] Encode(GameFrame frame) { Frames.Add(frame); onEncode(frame); return [0x71]; }
        public GameFrame Decode(ReadOnlySpan<byte> payload) { Decodes++; throw new InvalidOperationException("Unexpected map-service decode"); }
    }
}
